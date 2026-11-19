using MyMailBox.Contracts;

namespace MyMailBox.Builtins;

internal static class UserGuide
{
    internal static Page Welcome()
    {
        using var file = typeof(UserGuide).Assembly.GetManifestResourceStream("MyMailBox.UserGuide")
            ?? throw new InvalidDataException("安装文件缺少使用指南。");
        using var reader = new StreamReader(file);
        var sections = reader.ReadToEnd().ReplaceLineEndings("\n").Split("\n## ").Skip(1)
            .Select(s => s.Split('\n', 2)).ToDictionary(s => s[0].Trim(), s => s[1].Trim(), StringComparer.Ordinal);
        var topics = sections.Keys.Select(s => new Choice(s, s)).ToArray();
        var version = typeof(UserGuide).Assembly.GetName().Version!.ToString(3);
        return new("welcome", "欢迎", $"MyMailBox {version} · 使用指南", [new("guide-topic", "选择帮助主题", topics[0].Id, topics)],
            [new("guide-read", "阅读步骤")], (values, _) => Task.FromResult(sections[values["guide-topic"]]));
    }
}
