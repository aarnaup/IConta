using System.Data;
using Dapper;

namespace Contabilidad.Infrastructure;

public static class TiposDapper
{
    private static bool _listo;

    public static void Inicializar()
    {
        if (_listo) return;
        DefaultTypeMap.MatchNamesWithUnderscores = true;   // canal_base_id -> CanalBaseId
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        _listo = true;
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => throw new InvalidCastException($"No se puede convertir {value.GetType()} a DateOnly")
        };
    }
}
