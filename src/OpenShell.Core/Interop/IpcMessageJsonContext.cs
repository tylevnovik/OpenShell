using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShell.Items;
using OpenShell.Paths;

namespace OpenShell.Interop;

/// <summary>
/// IPC JSON 序列化配置。Per ADR-0021 §1.
/// 提供长度前缀 JSON 协议使用的 <see cref="JsonSerializerOptions"/> 与 helper 方法。
/// <see cref="IpcMessage"/> 的多态序列化由基类上的 <c>[JsonPolymorphic]</c> attribute 接管;
/// <see cref="IItem"/> / <see cref="ItemPath"/> 通过自定义 <see cref="JsonConverter"/> 处理,
/// 避免修改 core domain 类型。
/// </summary>
public static class IpcMessageJsonContext
{
    private static readonly JsonSerializerOptions s_options = BuildOptions();

    /// <summary>共享的 JsonSerializerOptions (camelCase + 自定义 converters)。</summary>
    public static JsonSerializerOptions Options => s_options;

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // ADR-0021 约束: ReferenceHandler.IgnoreCycles, 避免 IItem 循环引用。
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            // 未知字段向后兼容 (旧版本忽略未知字段)。
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
        };
        opts.Converters.Add(new IpcItemPathConverter());
        opts.Converters.Add(new IpcItemConverter());
        return opts;
    }

    /// <summary>序列化 IpcMessage 为 JSON 字符串。</summary>
    public static string Serialize(IpcMessage message)
        => JsonSerializer.Serialize(message, typeof(IpcMessage), s_options);

    /// <summary>反序列化 JSON 字符串为 IpcMessage (多态分发)。</summary>
    public static IpcMessage? Deserialize(string json)
        => JsonSerializer.Deserialize<IpcMessage>(json, s_options);
}

/// <summary>
/// <see cref="ItemPath"/> 的精简 JSON converter。Per ADR-0021.
/// 序列化: 仅写 <c>{"provider":"fs","internalPath":"C:/Users"}</c> (省略 computed 属性 IsRooted/Display/FriendlyName)。
/// 反序列化: 通过 init setter 构造。
/// </summary>
internal sealed class IpcItemPathConverter : JsonConverter<ItemPath>
{
    public override ItemPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected StartObject for ItemPath, got {reader.TokenType}.");

        string? provider = null;
        string? internalPath = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var propName = reader.GetString();
            reader.Read();
            switch (propName)
            {
                case "provider": provider = reader.GetString(); break;
                case "internalPath": internalPath = reader.GetString(); break;
                // 忽略 isRooted / display / friendlyName (computed, 反序列化时由 Provider+InternalPath 重建)。
                default: reader.Skip(); break;
            }
        }

        return new ItemPath
        {
            Provider = provider ?? "fs",
            InternalPath = internalPath ?? "",
        };
    }

    public override void Write(Utf8JsonWriter writer, ItemPath value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("provider", value.Provider);
        writer.WriteString("internalPath", value.InternalPath);
        writer.WriteEndObject();
    }
}

/// <summary>
/// <see cref="IItem"/> 的 JSON converter。Per ADR-0021.
/// 序列化: 委派给具体类型 <see cref="Item"/> (项目唯一实现)。
/// 反序列化: 构造 <see cref="Item"/> 实例 (其他 IItem 实现的属性尽力保留)。
/// </summary>
internal sealed class IpcItemConverter : JsonConverter<IItem>
{
    public override IItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 委派给 Item (唯一已知具体类型); Item 的 Path 属性会走 IpcItemPathConverter。
        return JsonSerializer.Deserialize<Item>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, IItem value, JsonSerializerOptions options)
    {
        if (value is Item item)
        {
            // 委派给 Item 的默认序列化 (走 IpcItemPathConverter 处理 Path)。
            JsonSerializer.Serialize(writer, item, options);
            return;
        }

        // 未知 IItem 实现: 写最小化 DTO (path / name / kind)。
        writer.WriteStartObject();
        writer.WriteString("path", value.Path.Display);
        writer.WriteString("name", value.Name);
        writer.WriteString("kind", value.Kind.ToString());
        writer.WriteEndObject();
    }
}
