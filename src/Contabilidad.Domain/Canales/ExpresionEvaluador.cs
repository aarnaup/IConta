using System.Globalization;

namespace Contabilidad.Domain.Canales;

/// <summary>
/// Evaluador seguro de fórmulas de reparto. Soporta + - * / paréntesis, números (punto decimal),
/// variables (IMPORTE, DEBE, HABER...) y funciones MIN, MAX, ABS, ROUND. No ejecuta código arbitrario.
/// Ejemplo: "ROUND(IMPORTE * 0.6, 2)"
/// </summary>
public static class ExpresionEvaluador
{
    public static decimal Evaluar(string expresion, IReadOnlyDictionary<string, decimal> variables)
    {
        if (string.IsNullOrWhiteSpace(expresion))
            throw new ReglaContableException("La fórmula está vacía.");
        return new Parser(expresion, variables).Evaluar();
    }

    private sealed class Parser
    {
        private readonly string _s;
        private readonly Dictionary<string, decimal> _vars;
        private int _p;

        public Parser(string s, IReadOnlyDictionary<string, decimal> vars)
        {
            _s = s;
            _vars = new Dictionary<string, decimal>(vars, StringComparer.OrdinalIgnoreCase);
        }

        public decimal Evaluar()
        {
            var r = Suma();
            Espacios();
            if (_p < _s.Length) throw Error($"Carácter inesperado '{_s[_p]}'");
            return r;
        }

        private decimal Suma()
        {
            var r = Producto();
            while (true)
            {
                Espacios();
                if (Toma('+')) r += Producto();
                else if (Toma('-')) r -= Producto();
                else return r;
            }
        }

        private decimal Producto()
        {
            var r = Unario();
            while (true)
            {
                Espacios();
                if (Toma('*')) r *= Unario();
                else if (Toma('/'))
                {
                    var d = Unario();
                    if (d == 0) throw Error("División por cero");
                    r /= d;
                }
                else return r;
            }
        }

        private decimal Unario()
        {
            Espacios();
            if (Toma('-')) return -Unario();
            if (Toma('+')) return Unario();
            return Primario();
        }

        private decimal Primario()
        {
            Espacios();
            if (Toma('('))
            {
                var r = Suma();
                Espacios();
                if (!Toma(')')) throw Error("Falta ')'");
                return r;
            }
            if (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.')) return Numero();
            if (_p < _s.Length && (char.IsLetter(_s[_p]) || _s[_p] == '_')) return Identificador();
            throw Error("Se esperaba un número, variable o '('");
        }

        private decimal Numero()
        {
            var ini = _p;
            while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.')) _p++;
            if (!decimal.TryParse(_s.AsSpan(ini, _p - ini), NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                throw Error("Número no válido");
            return n;
        }

        private decimal Identificador()
        {
            var ini = _p;
            while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_')) _p++;
            var nombre = _s[ini.._p];
            Espacios();
            if (!Toma('('))
            {
                if (_vars.TryGetValue(nombre, out var v)) return v;
                throw Error($"Variable desconocida '{nombre}'");
            }

            var args = new List<decimal>();
            Espacios();
            if (!Toma(')'))
            {
                do { args.Add(Suma()); Espacios(); } while (Toma(','));
                if (!Toma(')')) throw Error("Falta ')' en la función");
            }

            return nombre.ToUpperInvariant() switch
            {
                "MIN" when args.Count >= 2 => args.Min(),
                "MAX" when args.Count >= 2 => args.Max(),
                "ABS" when args.Count == 1 => Math.Abs(args[0]),
                "ROUND" when args.Count == 2 => Math.Round(args[0], (int)args[1], MidpointRounding.AwayFromZero),
                _ => throw Error($"Función '{nombre}' no válida o con argumentos incorrectos")
            };
        }

        private void Espacios() { while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++; }
        private bool Toma(char c) { if (_p < _s.Length && _s[_p] == c) { _p++; return true; } return false; }
        private ReglaContableException Error(string m) => new($"Fórmula no válida: {m}", $"'{_s}'");
    }
}
