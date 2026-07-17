using FluentAssertions;
using OpenShell.Favorites;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.Favorites;

/// <summary>
/// ADR-0028 §6: FileFavoritesService 单元测试。验证 Add / Remove / Reload /
/// 持久化 / 大小写不敏感 / 事件触发 / 损坏文件降级。
/// </summary>
public sealed class FileFavoritesServiceTests
{
    [Fact]
    public void Constructor_MissingFile_ReturnsEmpty()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);
        svc.Favorites.Should().BeEmpty();
    }

    [Fact]
    public void Add_SingleFavorite_PersistsToFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        svc.Add(new Favorite("Projects", "fs::C:/Users/me/Projects"));

        File.Exists(path).Should().BeTrue();
        var content = File.ReadAllText(path);
        content.Should().Contain("Projects");
        content.Should().Contain("fs::C:/Users/me/Projects");
    }

    [Fact]
    public void Add_DuplicateName_ReplacesExisting()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        svc.Add(new Favorite("Projects", "fs::C:/Old"));
        svc.Add(new Favorite("Projects", "fs::C:/New"));

        svc.Favorites.Should().ContainSingle();
        svc.Favorites[0].Path.Should().Be("fs::C:/New");
    }

    [Fact]
    public void Add_DuplicateName_CaseInsensitive_ReplacesExisting()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        svc.Add(new Favorite("Projects", "fs::C:/Old"));
        svc.Add(new Favorite("PROJECTS", "fs::C:/New"));

        svc.Favorites.Should().ContainSingle();
        svc.Favorites[0].Name.Should().Be("PROJECTS");
        svc.Favorites[0].Path.Should().Be("fs::C:/New");
    }

    [Fact]
    public void Remove_Existing_ReturnsTrueAndPersists()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);
        svc.Add(new Favorite("Projects", "fs::C:/Projects"));

        var result = svc.Remove("Projects");

        result.Should().BeTrue();
        svc.Favorites.Should().BeEmpty();
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        var result = svc.Remove("does-not-exist");

        result.Should().BeFalse();
    }

    [Fact]
    public void Remove_CaseInsensitiveNameLookup()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);
        svc.Add(new Favorite("Projects", "fs::C:/Projects"));

        svc.Remove("PROJECTS").Should().BeTrue();
        svc.Favorites.Should().BeEmpty();
    }

    [Fact]
    public void Reload_ReReadsFromFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc1 = new FileFavoritesService(path);
        svc1.Add(new Favorite("A", "fs::C:/A"));

        // 用同一文件构造第二个实例 (模拟外部修改)。
        var svc2 = new FileFavoritesService(path);
        svc2.Favorites.Should().ContainSingle();
        svc2.Favorites[0].Name.Should().Be("A");

        // 通过 svc1 再加一条, 然后让 svc2 reload。
        svc1.Add(new Favorite("B", "fs::C:/B"));
        svc2.Favorites.Should().HaveCount(1); // 仍为旧视图
        svc2.Reload();
        svc2.Favorites.Should().HaveCount(2);
        svc2.Favorites.Should().Contain(f => f.Name == "A");
        svc2.Favorites.Should().Contain(f => f.Name == "B");
    }

    [Fact]
    public void Reload_MissingFile_EmptyList()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        svc.Reload();
        svc.Favorites.Should().BeEmpty();
    }

    [Fact]
    public void Reload_InvalidToml_EmptyList_NoThrow()
    {
        using var dir = new TempDir();
        var path = dir.CreateFile("favorites.toml", "garbage {{{ not toml");
        var svc = new FileFavoritesService(path);

        var act = () => svc.Reload();
        act.Should().NotThrow();
        svc.Favorites.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_InvalidToml_EmptyList_NoThrow()
    {
        using var dir = new TempDir();
        var path = dir.CreateFile("favorites.toml", "garbage {{{ not toml");

        var act = () => new FileFavoritesService(path);
        act.Should().NotThrow();
        var svc = new FileFavoritesService(path);
        svc.Favorites.Should().BeEmpty();
    }

    [Fact]
    public void FavoritesChanged_FiresOnAdd()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        var fired = 0;
        svc.FavoritesChanged += (s, e) => fired++;

        svc.Add(new Favorite("A", "fs::C:/A"));
        fired.Should().Be(1);
    }

    [Fact]
    public void FavoritesChanged_FiresOnRemove()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);
        svc.Add(new Favorite("A", "fs::C:/A"));

        var fired = 0;
        svc.FavoritesChanged += (s, e) => fired++;

        svc.Remove("A");
        fired.Should().Be(1);
    }

    [Fact]
    public void FavoritesChanged_DoesNotFire_OnRemoveNonExistent()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        var fired = 0;
        svc.FavoritesChanged += (s, e) => fired++;

        svc.Remove("nope");
        fired.Should().Be(0);
    }

    [Fact]
    public void FavoritesChanged_FiresOnReload()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        var fired = 0;
        svc.FavoritesChanged += (s, e) => fired++;

        svc.Reload();
        fired.Should().Be(1);
    }

    [Fact]
    public void MultipleFavorites_RoundTrip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc1 = new FileFavoritesService(path);
        svc1.Add(new Favorite("Projects", "fs::C:/Users/me/Projects"));
        svc1.Add(new Favorite("S3 Backup", "s3://my-backup-bucket"));
        svc1.Add(new Favorite("Notes", "fs::C:/Notes"));

        // 新实例从同一文件加载, 验证 round-trip。
        var svc2 = new FileFavoritesService(path);
        svc2.Favorites.Should().HaveCount(3);
        svc2.Favorites[0].Name.Should().Be("Projects");
        svc2.Favorites[0].Path.Should().Be("fs::C:/Users/me/Projects");
        svc2.Favorites[1].Name.Should().Be("S3 Backup");
        svc2.Favorites[1].Path.Should().Be("s3://my-backup-bucket");
        svc2.Favorites[2].Name.Should().Be("Notes");
        svc2.Favorites[2].Path.Should().Be("fs::C:/Notes");
    }

    [Fact]
    public void Save_WritesTomlTableArrayFormat()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        svc.Add(new Favorite("Projects", "fs::C:/Projects"));
        svc.Add(new Favorite("S3 Backup", "s3://my-backup-bucket"));

        var content = File.ReadAllText(path);
        // 验证 [[favorite]] 数组表格式被写入 (每个条目占一个 [[favorite]] 表头)。
        content.Should().Contain("[[favorite]]");
        content.Should().Contain("name = \"Projects\"");
        content.Should().Contain("path = \"fs::C:/Projects\"");
        content.Should().Contain("name = \"S3 Backup\"");
        content.Should().Contain("path = \"s3://my-backup-bucket\"");
        // 计数 [[favorite]] 出现次数应为 2。
        var occurrences = content.Split("[[favorite]]", StringSplitOptions.None).Length - 1;
        occurrences.Should().Be(2);
    }

    [Fact]
    public void Add_NullFavorite_Throws()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        var act = () => svc.Add(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Add_EmptyName_Throws()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        var act = () => svc.Add(new Favorite("", "fs::C:/X"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Add_EmptyPath_Throws()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);

        var act = () => svc.Add(new Favorite("X", ""));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Remove_EmptyName_ReturnsFalse()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.FullPath, "favorites.toml");
        var svc = new FileFavoritesService(path);
        svc.Add(new Favorite("A", "fs::C:/A"));

        svc.Remove("").Should().BeFalse();
        svc.Favorites.Should().ContainSingle();
    }

    [Fact]
    public void Reload_SkipsEntriesMissingFields()
    {
        using var dir = new TempDir();
        // 缺 path 字段的条目应被跳过。
        var path = dir.CreateFile("favorites.toml", """
            [[favorite]]
            name = "good"
            path = "fs::C:/good"

            [[favorite]]
            name = "no-path"

            [[favorite]]
            path = "fs::C:/no-name"
            """);
        var svc = new FileFavoritesService(path);

        svc.Favorites.Should().ContainSingle();
        svc.Favorites[0].Name.Should().Be("good");
        svc.Favorites[0].Path.Should().Be("fs::C:/good");
    }
}
