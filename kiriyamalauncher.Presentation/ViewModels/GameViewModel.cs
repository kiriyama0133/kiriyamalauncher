using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using System;
using System.IO;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 游戏卡片（<see cref="Base.Controls.GameCard"/>）的 ViewModel：
/// 把 Business 层给的 <see cref="GameDto"/> 转换成界面需要的文本与图片。
/// </summary>
public partial class GameViewModel : ObservableObject
{
    private const string COVER_IMAGE_FOLDER = "Base/Assets/Covers/";
    private const string ASSEMBLY_ROOT = "avares://kiriyamalauncher.Presentation/";

    private readonly Action<GameViewModel> _openRequested;

    public GameDto Game { get; }

    public string Name => Game.Name;

    public string Description => Game.Description;

    /// <summary>封面图（Business 只给标识，这里解析成位图；解析失败时为 null）。</summary>
    public Bitmap? CoverImage { get; }

    public bool HasCoverImage => CoverImage is not null;

    public GameViewModel(GameDto game, Action<GameViewModel> openRequested)
    {
        Game = game;
        _openRequested = openRequested;
        CoverImage = LoadCoverImage(game.CoverImage);
    }

    [RelayCommand]
    private void Open() => _openRequested(this);

    /// <summary>把 Business 给的封面标识解析成位图：avares:// 资源、本地文件都支持，失败则返回 null（界面回退到占位图）。</summary>
    private static Bitmap? LoadCoverImage(string? coverImage)
    {
        if (string.IsNullOrWhiteSpace(coverImage))
        {
            return null;
        }

        string source = coverImage.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(coverImage)
            ? coverImage
            : $"{ASSEMBLY_ROOT}{COVER_IMAGE_FOLDER}{coverImage}";

        try
        {
            return Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeFile
                ? new Bitmap(uri.LocalPath)
                : uri is not null && uri.Scheme == "avares"
                    ? new Bitmap(AssetLoader.Open(uri))
                    : new Bitmap(source);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
