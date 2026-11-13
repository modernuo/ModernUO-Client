namespace Uo.Render;

/// <summary>The inputs to settled streaming maintenance, including the motion-biased prefetch rectangle.</summary>
public struct RenderViewReuse
{
    RectI _view, _need;
    float _scale;
    long _chunks;
    int _art, _anim;
    bool _valid;

    public readonly bool Matches(bool enabled, in RectI view, in RectI need, float scale,
        long chunks, int art, int anim, bool settled)
        => enabled && settled && _valid && Same(_view, view) && Same(_need, need) && _scale == scale
            && _chunks == chunks && _art == art && _anim == anim;

    public void Remember(in RectI view, in RectI need, float scale, long chunks, int art, int anim)
    {
        _view = view; _need = need; _scale = scale; _chunks = chunks; _art = art; _anim = anim; _valid = true;
    }

    static bool Same(in RectI a, in RectI b) => a.X0 == b.X0 && a.Y0 == b.Y0 && a.X1 == b.X1 && a.Y1 == b.Y1;
}
