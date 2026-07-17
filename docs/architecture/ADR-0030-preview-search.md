# ADR-0030: 预览面板与搜索

- **Status**: Accepted
- **Date**: 2026-07-07
- **Stage**: M3
- **Decider**: Architecture
- **Supersedes**: —
- **Related**: ADR-0015 (虚拟化), ADR-0027 (快捷键), ADR-0028 (菜单)

## Context

GUI 文件管理器需要：

1. **预览面板**：右侧/底部显示选中项预览
2. **快速预览**：空格键弹全屏预览（macOS Quick Look 风格）
3. **文件名搜索**：Everything 风格实时模糊匹配
4. **内容搜索**：grep 风格搜索文件内容
5. **预览类型**：图片 / 文本 / PDF / 视频 / 音频 / zip 内容 / 代码（语法高亮）
6. **大文件**：流式预览，不全加载
7. **远程**：远程文件预览需下载部分内容
8. **缓存**：预览结果缓存

挑战：
- 不同类型预览策略差异大
- 搜索需平衡延迟与完整性
- 内容搜索在远程 Provider 难

## Decision

### 1. 预览面板

#### IPreviewService

```csharp
public interface IPreviewService
{
    /// <summary>是否能预览。</summary>
    bool CanPreview(IItem item);

    /// <summary>异步生成预览视图模型。</summary>
    ValueTask<PreviewViewModel?> CreatePreviewAsync(IItem item, PreviewOptions options, CancellationToken ct);
}

public sealed record PreviewOptions(
    int MaxWidth = 400,
    int MaxHeight = 300,
    bool WithMetadata = true);

public abstract record PreviewViewModel
{
    public sealed record Image(IBitmap Bitmap, int Width, int Height) : PreviewViewModel;
    public sealed record Text(string Content, string? Language) : PreviewViewModel;
    public sealed record Pdf(int PageCount, IBitmap FirstPageThumbnail) : PreviewViewModel;
    public sealed record Video(TimeSpan Duration, IBitmap Thumbnail) : PreviewViewModel;
    public sealed record Archive(IReadOnlyList<IItem> Entries) : PreviewViewModel;
    public sealed record NotSupported(string Reason) : PreviewViewModel;
}
```

#### 预览面板位置

- 默认右侧（200-400px 宽，可拖拽调整）
- 可切到底部
- 可隐藏（`View > Preview Panel`）

#### 快速预览（空格键）

- 选中文件按空格 → 弹出大尺寸预览窗口
- 窗口居中，含上一项/下一项按钮
- 再按空格关闭
- macOS Quick Look 行为

### 2. 预览器实现

#### 图片预览器

```csharp
public sealed class ImagePreviewer : IPreviewer
{
    public bool CanPreview(IItem item)
        => item.ContentType?.StartsWith("image/") == true
           || IsImageExtension(item.Path);

    public async ValueTask<PreviewViewModel> CreatePreviewAsync(IItem item, ...)
    {
        var stream = await contentProvider.OpenReadAsync(item.Path, ct);
        var bitmap = await Bitmap.LoadAsync(stream);
        // 大图缩放
        var scaled = bitmap.CreateScaledBitmap(new PixelSize(400, 300), BitmapInterpolationMode.HighQuality);
        return new PreviewViewModel.Image(scaled, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
    }
}
```

支持格式：PNG / JPEG / GIF / BMP / WEBP / SVG（Avalonia 内置 + Svg.Skia）。

#### 文本预览器

- 前 1000 行 + 总行数显示
- 文件 > 1MB 不全加载，提示"showing first 1000 lines"
- 语法高亮（Markdown / JSON / XML / C# / Python / JS，使用 AvalonEdit 或 TextMate）
- 编码自动检测（UTF-8 / GBK / Shift-JIS）
- 二进制检测（前 8KB 含 `\0` 判定为二进制，显示 hex dump）

#### PDF 预览器

- 第一页缩略图 + 总页数
- 点击"Open in default app"
- 实现依赖 PDFium（NuGet `PdfiumViewer` 或 `SkiaSharp` PDF 支持）

#### 视频预览器

- 第一帧缩略图 + 时长
- 实现：ffmpeg 提取首帧（需 ffmpeg 二进制可访问）

#### Archive 预览器

- 列出包内前 100 个 entry
- 双击 entry 走 ADR-0017 路径访问

#### 代码预览器

- 按扩展名识别语言
- TextMate 语法高亮
- 行号显示
- 大文件仅前 1000 行

### 3. 预览缓存

`~/.openshell/cache/previews/`：

- key = `ItemPath.Display + Modified timestamp` 的 SHA256
- value = 缩略图位图
- LRU 1000 张

### 4. 文件名搜索

#### ISearchService

```csharp
public interface IFileNameSearchService
{
    /// <summary>流式返回匹配项，延迟 < 50ms。</summary>
    IAsyncEnumerable<IItem> SearchAsync(
        ItemPath root,
        string query,
        SearchOptions options,
        CancellationToken ct);
}

public sealed record SearchOptions(
    bool Recurse = true,
    int MaxResults = 1000,
    bool IncludeHidden = false,
    bool FuzzyMatch = true);
```

#### 搜索框

PaneView 顶部搜索框：

- 输入实时搜索（300ms 防抖）
- 结果替换当前列表视图（不修改 CurrentLocation）
- Esc 清空搜索，恢复目录视图
- 显示"X results in Y ms"

#### 搜索算法

**简单子串匹配**（默认）：

```csharp
name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
```

**模糊匹配**（可选）：

- fzf 风格子序列评分
- 高亮匹配字符

**Everything 风格索引**（M3+）：

- 后台索引磁盘（USN Journal，Windows）
- 启动时加载索引到内存
- 查询 < 10ms

M3 仅简单匹配，索引留 M5+ 评估。

### 5. 内容搜索

`search-content` 命令：

```
search-content -path fs::C:/Users -pattern "TODO" -include "*.cs"
```

```csharp
[Verb("Search", Noun = "Content")]
public sealed class SearchContentCommand : ICommand<...>
{
    public async IAsyncEnumerable<IItem> ExecuteAsync(...)
    {
        await foreach (var file in EnumerateFiles(args.Path, args.Include, ct))
        {
            var matches = await GrepFileAsync(file, args.Pattern, ct);
            if (matches.Count > 0)
                yield return ToSearchResultItem(file, matches);
        }
    }
}
```

#### 实现

- 文本文件用 `StreamReader` 流式扫描
- 二进制跳过
- 大文件不全加载
- 并发（默认 4 线程）
- 进度更新

#### 远程内容搜索

- S3：`SelectObjectContent` API（如服务端支持）
- WebDAV：不支持，需下载
- SFTP：`grep` 远程执行（如服务端支持）

### 6. 全局搜索

Ctrl+Shift+F（全局搜索）：

- 弹出搜索面板
- 选择范围（当前位置 / 收藏夹 / 全盘）
- 文件名 + 内容组合
- 结果聚合显示，双击跳转

### 7. 搜索结果项

```csharp
public sealed record SearchResultItem : IItem
{
    public required ItemPath Path { get; init; }
    public ItemKind Kind => ItemKind.File;
    public string Name => Path.GetName();
    public PropertyBag Properties { get; init; }
    // 含搜索上下文：
    //   matchedLines: [{line:42, text:"// TODO: fix this"}]
    //   score: 0.85
}
```

### 8. 索引（远期）

`~/.openshell/index/`：

- SQLite 或 Lucene 索引
- 后台 watcher 监视文件变化更新索引
- 启动时加载
- 占用磁盘但查询极快

M3 不实现，文档明确说明。

### 9. 性能预算

| 场景 | 目标 |
|---|---|
| 图片预览加载 | < 100ms（缓存命中）/ < 500ms（解码） |
| 文本预览加载 | < 50ms |
| 文件名搜索（1000 项） | < 50ms |
| 内容搜索（1000 文件） | < 5s |
| 快速预览切换 | < 200ms |

## Alternatives Considered

1. **仅文件名 + 大小预览**：被否决，体验差
2. **预览完全靠外部应用**：被否决，集成度低
3. **每次搜索都全盘扫描**：被否决，性能不可接受
4. **完整 Lucene 索引**：被否决，M3 复杂度过高
5. **不实现内容搜索**：被否决，PowerShell 有 `Select-String`，常见需求

## Consequences

### 优势
- 多类型预览
- 实时搜索
- 内容搜索支持
- 快速预览提升体验
- 缓存避免重复解码

### 代价
- 多种预览器维护成本
- PDF / 视频预览依赖外部库
- 内容搜索性能限制
- 远程内容搜索复杂

### 约束
- 预览生成必须有超时（默认 5s）
- 预览缓存 LRU 上限 1000 张
- 文本预览必须流式读取，禁止全加载
- 二进制检测必须在前 8KB 完成
- 搜索结果必须支持取消
- 模糊匹配必须可关闭（性能 / 精确需求）
- 远程搜索必须明确告知"may be slow"
- 快速预览窗口必须支持 Esc 关闭
- 预览生成失败时显示"Not Supported"原因
- PDF 视频预览依赖库必须可选（启动时检测，无则降级）
- 全局搜索结果必须按文件分组，便于浏览
