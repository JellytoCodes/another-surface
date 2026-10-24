namespace Vellume.Core;

public sealed class StickerItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AssetName { get; set; } = string.Empty;
    public bool IsVisible { get; set; } = true;
    // Size is the unrotated square side on the existing 1920x1080 widget canvas.
    public double Size { get; set; } = 240;
    public double Rotation { get; set; }
    public WidgetPlacement Placement { get; set; } =
        WidgetPlacement.FromAnchor(WidgetAnchor.Center, false);

    public StickerItem Copy() => new()
    {
        Id = Id,
        AssetName = AssetName,
        IsVisible = IsVisible,
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 48, 720) : 240,
        Rotation = double.IsFinite(Rotation) ? ((Rotation % 360) + 540) % 360 - 180 : 0,
        Placement = (Placement ?? new WidgetPlacement()).Copy(WidgetAnchor.Center)
    };

    public double GetBoundsSize()
    {
        var normalized = Copy();
        var angle = normalized.Rotation * Math.PI / 180;
        return normalized.Size * (Math.Abs(Math.Cos(angle)) + Math.Abs(Math.Sin(angle)));
    }
}

public sealed record StickerAsset(string FileName, string Name)
{
    public string ImagePath => Path.Combine(AppContext.BaseDirectory, "Assets", "Stickers", FileName);
}

public static class StickerCatalog
{
    public static IReadOnlyList<StickerAsset> All { get; } = CreateAssets();

    public static StickerAsset? Find(string fileName) => All.FirstOrDefault(
        asset => string.Equals(asset.FileName, fileName, StringComparison.Ordinal));

    private static IReadOnlyList<StickerAsset> CreateAssets()
    {
        string[] characters = ["Ena", "Anju", "Tsukimi", "Ririko", "Miku", "Nayuka"];
        string[] names = ["에나", "안주", "츠키미", "리리코", "미쿠", "나유카"];
        string[] kinds = ["Icon", "Sticker", "Poster", "Band"];
        string[] labels = ["아이콘", "스티커", "포스터", "밴드"];
        var assets = new List<StickerAsset>();
        for (var character = 0; character < characters.Length; character++)
            for (var kind = 0; kind < kinds.Length; kind++)
                assets.Add(new StickerAsset(
                    $"{assets.Count + 1:00}_{characters[character]}_{kinds[kind]}.png",
                    $"{names[character]} · {labels[kind]}"));
        assets.Add(new StickerAsset("25_LemonadeFactory_Common.png", "레모네이드 팩토리"));
        return assets.AsReadOnly();
    }
}

public sealed class StickerChangedEventArgs(StickerItem sticker, bool isLockChange = false, bool isVisibilityChange = false) : EventArgs
{
    public StickerItem Sticker { get; } = sticker.Copy();
    public bool IsLockChange { get; } = isLockChange;
    public bool IsVisibilityChange { get; } = isVisibilityChange;
}
