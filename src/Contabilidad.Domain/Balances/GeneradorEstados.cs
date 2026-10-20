namespace Contabilidad.Domain.Balances;

/// <summary>Saldo de una cuenta (debe − haber) en el canal y fechas pedidos.</summary>
public sealed record SaldoBalance(string Codigo, string Nombre, decimal Saldo);

public sealed record CuentaEnPartida(string Codigo, string Nombre, decimal Importe);

public sealed record PartidaCalculada(string Id, string Texto, int Nivel, bool EsTotal, decimal Importe, IReadOnlyList<CuentaEnPartida> Cuentas);

public sealed record EstadoCalculado(IReadOnlyList<PartidaCalculada> Partidas, IReadOnlyList<SaldoBalance> SinClasificar)
{
    public decimal this[string id] => Partidas.First(p => p.Id == id).Importe;
}

/// <summary>Reparte los saldos de las cuentas entre las partidas de un modelo y calcula subtotales y totales.</summary>
public static class GeneradorEstados
{
    private sealed record Regla(string Prefijo, char? Lado, PartidaModelo Partida);

    public static EstadoCalculado Calcular(ModeloEstado modelo, IEnumerable<SaldoBalance> saldos)
    {
        var reglas = modelo.Partidas
            .Where(p => p.Cuentas is not null)
            .SelectMany(p => p.Cuentas!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t =>
            {
                var partes = t.Split(':');
                return new Regla(partes[0], partes.Length > 1 ? partes[1][0] : null, p);
            }))
            .OrderByDescending(r => r.Prefijo.Length)
            .ToList();

        var porPartida = modelo.Partidas.ToDictionary(p => p.Id, _ => new List<CuentaEnPartida>());
        var sinClasificar = new List<SaldoBalance>();

        foreach (var s in saldos.Where(s => s.Saldo != 0 && modelo.Alcance.Any(a => s.Codigo.StartsWith(a, StringComparison.Ordinal)))
                                .OrderBy(s => s.Codigo, StringComparer.Ordinal))
        {
            var regla = reglas.FirstOrDefault(r => s.Codigo.StartsWith(r.Prefijo, StringComparison.Ordinal)
                                                   && (r.Lado is null || (r.Lado == 'D' ? s.Saldo > 0 : s.Saldo < 0)));
            if (regla is null) { sinClasificar.Add(s); continue; }
            porPartida[regla.Partida.Id].Add(new CuentaEnPartida(s.Codigo, s.Nombre, regla.Partida.Acreedora ? -s.Saldo : s.Saldo));
        }

        // Los totales pueden sumar partidas definidas después: se resuelven con memoria
        var importes = new Dictionary<string, decimal>();
        var definicion = modelo.Partidas.ToDictionary(p => p.Id);
        decimal Importe(string id)
        {
            if (importes.TryGetValue(id, out var v)) return v;
            var p = definicion[id];
            v = p.Suma is null
                ? porPartida[id].Sum(c => c.Importe)
                : p.Suma.Split(' ', StringSplitOptions.RemoveEmptyEntries).Sum(Importe);
            return importes[id] = v;
        }

        var partidas = modelo.Partidas
            .Select(p => new PartidaCalculada(p.Id, p.Texto, p.Nivel, p.EsTotal, Importe(p.Id), porPartida[p.Id]))
            .ToList();
        return new EstadoCalculado(partidas, sinClasificar);
    }
}
