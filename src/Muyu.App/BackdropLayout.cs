using System.Numerics;

namespace Muyu.App;

/// <summary>Offscreen coverage for a rotating square, including the full drift and blur footprint.</summary>
internal static class BackdropLayout
{
    internal const float BlurGuard=96;
    internal readonly record struct Layer(float Side,Vector3 Offset,Vector3 Pivot);
    internal static Vector3 Drift(int layer)=>new(layer==0?.10f:-.10f,.08f,0);
    internal static Layer Calculate(Vector2 viewport,int layer)
    {
        var center=viewport*new Vector2(layer==0?.5f:.6f,.5f);
        var motion=Drift(layer);var end=center+viewport*new Vector2(motion.X,motion.Y);
        float Reach(float c,float e,float length)=>Math.Max(Math.Max(Math.Abs(c),Math.Abs(length-c)),Math.Max(Math.Abs(e),Math.Abs(length-e)));
        float x=Reach(center.X,end.X,viewport.X),y=Reach(center.Y,end.Y,viewport.Y);
        float side=2*(MathF.Sqrt(x*x+y*y)+BlurGuard);
        var pivot=new Vector3(side/2,side/2,0);
        return new(side,new Vector3(center,0)-pivot,pivot);
    }
}
