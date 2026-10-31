namespace Contabilidad.Domain.Balances;

/// <summary>Saldo de una cuenta (debe − haber) en el canal y fechas pedidos; con las sumas del debe y del haber si se conocen.</summary>
public sealed record SaldoBalance(string Codigo, string Nombre, decimal Saldo, decimal Debe = 0, decimal Haber = 0);

public sealed record CuentaEnPartida(string Codigo, string Nombre, decimal Importe);

public sealed record PartidaCalculada(string Id, string Texto, int Nivel, bool EsTotal, decimal Importe, IReadOnlyList<CuentaEnPartida> Cuentas);

public sealed record EstadoCalculado(IReadOnlyList<PartidaCalculada> Partidas, IReadOnlyList<SaldoBalance> SinClasificar)
{
    public decimal this[string id] => Partidas.First(p => p.Id == id).Importe;
}

/// <summary>Reparte los saldos de las cuentas entre las partidas de un modelo y calcula subtotales y totales.</summary>
public static class GeneradorEstados
{
    private sealed record Regla(ReglaCuenta R, PartidaModelo Partida);

    public static EstadoCalculado Calcular(ModeloEstado modelo, IEnumerable<SaldoBalance> saldos)
    {
        var reglas = modelo.Partidas
            .SelectMany(p => ReglaCuenta.Lista(p.Cuentas).Select(r => new Regla(r, p)))
            .OrderByDescending(r => r.R.Prefijo.Length)
            .ToList();

        var porPartida = modelo.Partidas.ToDictionary(p => p.Id, _ => new List<CuentaEnPartida>());
        var sinClasificar = new List<SaldoBalance>();

        foreach (var s in saldos.Where(s => (s.Saldo != 0 || s.Debe != 0 || s.Haber != 0)
                                            && modelo.Alcance.Any(a => s.Codigo.StartsWith(a, StringComparison.Ordinal)))
                                .OrderBy(s => s.Codigo, StringComparer.Ordinal))
        {
            // La cuenta va a las reglas del prefijo más largo que le aplica (puede haber varias: el debe a una partida y el haber a otra)
            var aplicables = reglas.Where(r => s.Codigo.StartsWith(r.R.Prefijo, StringComparison.Ordinal) && r.R.Aplica(s.Saldo)).ToList();
            if (aplicables.Count == 0) { if (s.Saldo != 0) sinClasificar.Add(s); continue; }
            var largo = aplicables[0].R.Prefijo.Length;
            foreach (var r in aplicables.TakeWhile(r => r.R.Prefijo.Length == largo))
            {
                var importe = r.R.Importe(s, r.Partida.Acreedora);
                if (importe != 0) porPartida[r.Partida.Id].Add(new CuentaEnPartida(s.Codigo, s.Nombre, importe));
            }
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

/// <summary>Qué se toma de la cuenta: el saldo, solo si es deudor o acreedor, o la suma del debe o del haber.</summary>
public enum ConceptoCuenta { Saldo, SaldoDeudor, SaldoAcreedor, Debe, Haber }

/// <summary>
/// Una cuenta de una partida. Se escribe como <c>prefijo</c>, con <c>:D</c> o <c>:A</c> (saldo deudor o acreedor), <c>:DEBE</c> o
/// <c>:HABER</c>, y con un «-» delante si resta. El saldo sigue el sentido de la partida: en una de activo o gasto es
/// debe − haber y en una de pasivo o ingreso, haber − debe (las cuentas correctoras restan solas). El debe y el haber son la suma de
/// los movimientos de ese lado. Sin «-» se suman a la partida y con él se restan.
/// </summary>
public sealed record ReglaCuenta(string Prefijo, ConceptoCuenta Concepto, bool Resta)
{
    public static IReadOnlyList<ReglaCuenta> Lista(string? cuentas)
        => (cuentas ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Leer).ToList();

    public static ReglaCuenta Leer(string t)
    {
        var resta = t.StartsWith('-');
        var partes = (resta ? t[1..] : t).Split(':');
        var concepto = partes.Length < 2 ? ConceptoCuenta.Saldo : partes[1].ToUpperInvariant() switch
        {
            "D" => ConceptoCuenta.SaldoDeudor,
            "A" => ConceptoCuenta.SaldoAcreedor,
            "DEBE" => ConceptoCuenta.Debe,
            "HABER" => ConceptoCuenta.Haber,
            "S" or "" => ConceptoCuenta.Saldo,
            var x => throw new ReglaContableException($"Concepto de cuenta no válido: {x} (en «{t}»)."),
        };
        if (partes[0].Length == 0 || !partes[0].All(char.IsAsciiDigit)) throw new ReglaContableException($"Cuenta no válida: «{t}».");
        return new ReglaCuenta(partes[0], concepto, resta);
    }

    public override string ToString() => (Resta ? "-" : "") + Prefijo + Concepto switch
    {
        ConceptoCuenta.SaldoDeudor => ":D", ConceptoCuenta.SaldoAcreedor => ":A", ConceptoCuenta.Debe => ":DEBE",
        ConceptoCuenta.Haber => ":HABER", _ => "",
    };

    public bool Aplica(decimal saldo) => Concepto switch
    {
        ConceptoCuenta.SaldoDeudor => saldo > 0,
        ConceptoCuenta.SaldoAcreedor => saldo < 0,
        _ => true,
    };

    public decimal Importe(SaldoBalance s, bool acreedora)
    {
        var v = Concepto switch { ConceptoCuenta.Debe => s.Debe, ConceptoCuenta.Haber => s.Haber, _ => acreedora ? -s.Saldo : s.Saldo };
        return Resta ? -v : v;
    }

    /// <summary>Cómo se calcula, para mostrarlo: «+ (Debe − Haber)», «− Haber»…</summary>
    public string Calculo(bool acreedora)
    {
        var texto = Concepto switch
        {
            ConceptoCuenta.Debe => "Debe",
            ConceptoCuenta.Haber => "Haber",
            _ => acreedora ? "(Haber − Debe)" : "(Debe − Haber)",
        };
        var cond = Concepto switch { ConceptoCuenta.SaldoDeudor => " si es deudor", ConceptoCuenta.SaldoAcreedor => " si es acreedor", _ => "" };
        return (Resta ? "− " : "+ ") + texto + cond;
    }
}
