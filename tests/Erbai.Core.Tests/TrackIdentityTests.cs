using Erbai.Contracts.Players;

namespace Erbai.Core.Tests;

/// <summary>曲目同一性判定（语义钉住）。</summary>
public class TrackIdentityTests
{
    private static PlayerTrack Track(string? id = null, string? title = null, string? artist = null) =>
        new()
        {
            Platform = "lxmusic",
            Id = id ?? "",
            Title = title ?? "",
            Artist = artist ?? "",
        };

    [Fact]
    public void StableIds_Different_AreAuthoritativelyDifferent()
    {
        var a = Track(id: "songmid-1", title: "September", artist: "Sparky");
        var b = Track(id: "songmid-2", title: "September", artist: "Sparky");
        Assert.False(TrackIdentity.TracksRepresentSame(a, b));
    }

    [Fact]
    public void StableIds_Same_AreSame()
    {
        var a = Track(id: "songmid-1", title: "A", artist: "B");
        var b = Track(id: "songmid-1", title: "A", artist: "B");
        Assert.True(TrackIdentity.TracksRepresentSame(a, b));
    }

    [Fact]
    public void MissingIds_TitleSubstringOrEqual_IsSame()
    {
        var a = Track(title: "September", artist: "Sparky Deathcap");
        var b = Track(title: "September (Live)", artist: "Sparky Deathcap");
        Assert.True(TrackIdentity.TracksRepresentSame(a, b));

        var c = Track(title: "September", artist: "Sparky Deathcap");
        var d = Track(title: "Cutie", artist: "Other");
        Assert.False(TrackIdentity.TracksRepresentSame(c, d));
    }

    [Fact]
    public void Normalization_FullWidthAndWhitespace_AreSame()
    {
        var a = Track(title: "Ｓｅｐｔｅｍｂｅｒ　纯音乐", artist: "A");
        var b = Track(title: "september 纯音乐", artist: "A");
        Assert.True(TrackIdentity.TracksRepresentSame(a, b));
    }

    [Fact]
    public void VersionAnnotations_DifferentNames_SamePrimaryTitle_IsSame()
    {
        // 搜索候选 "September (纯音乐)" vs 播放器实报 "September (Inst.)"
        var a = Track(title: "September (纯音乐)", artist: "Sparky Deathcap");
        var b = Track(title: "September (Inst.)", artist: "Sparky Deathcap");
        Assert.True(TrackIdentity.TracksRepresentSame(a, b));
    }

    [Fact]
    public void VersionAnnotations_DifferentPrimaryTitle_IsNotSame()
    {
        // 前缀同名歌曲不得被版本注释宽容匹配误判（"September" vs "September Rain"）；
        // 歌手也不同 → 明确不是同一首
        var a = Track(title: "September (纯音乐)", artist: "Sparky Deathcap");
        var b = Track(title: "September Rain (Inst.)", artist: "Other Artist");
        Assert.False(TrackIdentity.TracksRepresentSame(a, b));
    }

    [Fact]
    public void OneSideMissingTitle_IsSame_LenientToAvoidReinsert()
    {
        var a = Track(title: "September", artist: "Sparky Deathcap");
        var b = Track(title: "", artist: "Sparky Deathcap");
        Assert.True(TrackIdentity.TracksRepresentSame(a, b));
    }

    [Fact]
    public void SameArtist_BothTitlesClearlyDifferent_ArtistFallbackIsSame()
    {
        // 曲目同一性判定最后一步是歌手兜底：
        // 双方标题明显不同但歌手相同 → 仍视为同一首（宽松语义；
        // 「双方都有歌名且明显不同时歌手不算匹配」属于播放状态机的
        // _song_identifiers_match，阶段 2 实现）
        var a = Track(title: "September", artist: "Sparky Deathcap");
        var b = Track(title: "Cutie", artist: "Sparky Deathcap");
        Assert.True(TrackIdentity.TracksRepresentSame(a, b));
    }

    [Fact]
    public void NoTitles_SameArtist_IsSame()
    {
        var a = Track(title: "", artist: "Sparky Deathcap");
        var b = Track(title: "", artist: "Sparky Deathcap");
        Assert.True(TrackIdentity.TracksRepresentSame(a, b));
    }

    [Fact]
    public void NullTrack_Semantics()
    {
        Assert.True(TrackIdentity.TracksRepresentSame(null, null));
        Assert.False(TrackIdentity.TracksRepresentSame(null, Track(title: "A")));
        Assert.False(TrackIdentity.TracksRepresentSame(Track(title: "A"), null));
    }

    [Fact]
    public void PrimaryTitle_StripsVersionAnnotation()
    {
        Assert.Equal("september", TrackIdentity.PrimaryTitle("September (Inst.) (September (Inst.)|Sparky Deathcap)"));
        Assert.Equal("september", TrackIdentity.PrimaryTitle("September（纯音乐）"));
    }

    [Fact]
    public void PrimaryTitle_BracketLeading_FallsBackToWholeNormalized()
    {
        // 标题以括号开头（版本注释在前的形态）→ 去括号内容后整体归一化
        Assert.Equal("september", TrackIdentity.PrimaryTitle("（纯音乐）September"));
        Assert.Equal("september", TrackIdentity.PrimaryTitle("(Live) September"));
    }

    [Fact]
    public void HasVersionAnnotation_DetectsInstPureMusicLive()
    {
        Assert.True(TrackIdentity.HasVersionAnnotation("September (Inst.)"));
        Assert.True(TrackIdentity.HasVersionAnnotation("九月（纯音乐）"));
        Assert.True(TrackIdentity.HasVersionAnnotation("September (Live)"));
        Assert.False(TrackIdentity.HasVersionAnnotation("September"));
        Assert.False(TrackIdentity.HasVersionAnnotation("September Rain"));
    }
}
