using System.Data;
using System.Globalization;
using Dapper;

namespace NimbusSync.Engine.State;

/// <summary>SQLite has no date type; store ISO-8601 round-trip strings.</summary>
public sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        => parameter.Value = value.ToString("o", CultureInfo.InvariantCulture);

    public override DateTimeOffset Parse(object value)
        => DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed class NullableDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset?>
{
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset? value)
        => parameter.Value = value?.ToString("o", CultureInfo.InvariantCulture) ?? (object)DBNull.Value;

    public override DateTimeOffset? Parse(object value)
        => value is null or DBNull ? null
            : DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
