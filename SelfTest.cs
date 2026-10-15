using System.Diagnostics;

namespace EzView;

// `ezview --selftest`: checks the sequence grouping, which is the one piece of real parsing logic.
static class SelfTest
{
    public static void Run()
    {
        var d = @"C:\x\";
        var g = Media.Group([
            d + "shot.1002.exr", d + "shot.1001.exr", d + "shot.1003.exr",   // sequence (unsorted input)
            d + "plate_0001.dpx", d + "plate_0002.dpx",                      // exr/dpx also accept "_"
            d + "IMG_0001.jpg", d + "IMG_0002.jpg",                          // photo series: NOT a sequence
            d + "render.0001.png",                                           // single frame: plain file
            d + "clip.mp4", d + "notes.txt",                                 // video kept, unknown dropped
        ]);
        string Names() => string.Join(", ", g.Select(e => e.Name));
        Trace.Assert(g.Count == 6, Names());
        var shot = g.Single(e => e.Path.EndsWith("shot.1001.exr"));
        Trace.Assert(shot.IsSequence && shot.FirstFrame == 1001 && shot.Frames!.Length == 3 && shot.Frames[2].EndsWith("1003.exr"));
        Trace.Assert(shot.Name == "shot.[1001–1003].exr", shot.Name);
        Trace.Assert(g.Single(e => e.Path.Contains("plate")).Frames!.Length == 2);
        Trace.Assert(g.Count(e => e.Path.Contains("IMG_")) == 2 && g.All(e => !e.Path.Contains("IMG_") || !e.IsSequence));
        Trace.Assert(!g.Single(e => e.Path.Contains("render")).IsSequence);
        // When the local test pack exists: every 3D animation format must decode to the same scene as the .usda text.
        var anim = Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\testpack\3d\anim\");
        if (File.Exists(anim + "anim.usda"))
        {
            // Mesh order follows each file's prim order, so compare the per-mesh lines as a set.
            string Body(string f) => string.Join("\n", AnimScene.Load(anim + f).Summary().Split('\n').Skip(1).Order());
            var reference = Body("anim.usda");
            foreach (var f in new[] { "anim.usdc", "anim.usdz" }) Trace.Assert(Body(f) == reference, f + " differs from anim.usda");
            var abc = AnimScene.Load(anim + "anim.abc");
            Trace.Assert(abc.Meshes.Count == 3 && abc.FrameCount == 48 && abc.Meshes.Single(m => m.Name.Contains("Blob")).Points.Length == 48, "anim.abc");
            Console.WriteLine("anim formats ok");
        }
        Console.WriteLine("selftest ok: " + Names());
    }
}
