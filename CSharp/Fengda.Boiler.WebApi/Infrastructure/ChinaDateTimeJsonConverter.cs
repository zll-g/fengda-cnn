using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fengda.Boiler.WebApi.Infrastructure;

/// <summary>
/// 将 DateTime 统一按中国标准时间整秒格式读写。
/// </summary>
public sealed class ChinaDateTimeJsonConverter : JsonConverter<DateTime>
{
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    /// <inheritdoc />
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value))
        {
            throw new JsonException("时间格式无效，请使用 ISO 8601 时间格式。");
        }

        return value.ToOffset(ChinaOffset).DateTime;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var chinaTime = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), ChinaOffset);
        writer.WriteStringValue(chinaTime.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
    }
}
