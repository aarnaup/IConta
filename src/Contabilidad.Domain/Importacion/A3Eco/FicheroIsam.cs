namespace Contabilidad.Domain.Importacion.A3Eco;

/// <summary>
/// Lee los registros de un fichero de datos ISAM de Micro Focus (Net Express, IDXFORMAT 3 y 4), como los de a3ECO.
/// El fichero empieza con una cabecera de 128 bytes y sigue con los registros uno tras otro, alineados a 4 bytes.
/// Cada registro lleva delante 2 bytes (4 si la longitud máxima pasa de 4095) cuyo primer medio byte es el tipo
/// (4 normal, 5 reducido, 2 borrado…) y el resto, la longitud. Para leer los datos no hace falta el índice (.idx).
/// </summary>
public static class FicheroIsam
{
    private const int Cabecera = 128;

    public static IReadOnlyList<byte[]> Registros(byte[] fichero, string nombre = "")
    {
        if (fichero.Length < Cabecera) return [];
        // La cabecera es un registro de tipo 3: «30 7E» con cabeceras de 2 bytes y «33 00 00 7C» con cabeceras de 4
        var cuatro = fichero[0] == 0x33 && fichero[1] == 0 && fichero[2] == 0 && fichero[3] == 0x7C;
        if (!cuatro && !(fichero[0] == 0x30 && fichero[1] == 0x7E))
            throw new ReglaContableException($"{nombre}: no es un fichero ISAM de Micro Focus.");
        if (fichero[41] != 0)
            throw new ReglaContableException($"{nombre}: el fichero está comprimido y no se puede leer.",
                "Descomprímelo con las utilidades de a3 o pide la copia sin compresión.");

        var lista = new List<byte[]>();
        var pos = Cabecera;
        var tamCab = cuatro ? 4 : 2;
        while (pos + tamCab <= fichero.Length)
        {
            var h = cuatro
                ? (fichero[pos] << 24) | (fichero[pos + 1] << 16) | (fichero[pos + 2] << 8) | fichero[pos + 3]
                : (fichero[pos] << 8) | fichero[pos + 1];
            if (h == 0) { pos += 4; continue; }                       // relleno hasta el siguiente registro
            var tipo = cuatro ? (h >>> 28) & 0xF : h >> 12;
            var largo = cuatro ? h & 0x0FFFFFFF : h & 0x0FFF;
            if (pos + tamCab + largo > fichero.Length)
                throw new ReglaContableException($"{nombre}: el fichero está cortado o dañado (posición {pos}).");
            if (tipo is 4 or 5) lista.Add(fichero.AsSpan(pos + tamCab, largo).ToArray());
            pos += tamCab + largo;
            if (pos % 4 != 0) pos += 4 - pos % 4;
        }
        return lista;
    }
}
