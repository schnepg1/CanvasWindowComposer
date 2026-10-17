using System;

namespace CanvasDesktop;

/// <summary>Finite, sampled travel between tutorial stops, independent of input and saved cameras.</summary>
internal sealed class TutorialCameraFlight(CanvasCamera from,CanvasCamera to)
{
    public const double Duration=.7;
    public CanvasCamera Sample(double seconds)
    {
        double t=double.IsFinite(seconds)?Math.Clamp(seconds/Duration,0,1):0;
        if(t==0)return from;if(t==1)return to;
        double p=t*t*t*(t*(t*6-15)+10);
        return new(from.X+(to.X-from.X)*p,from.Y+(to.Y-from.Y)*p,
            Math.Exp(Math.Log(from.Zoom)+(Math.Log(to.Zoom)-Math.Log(from.Zoom))*p));
    }
}
