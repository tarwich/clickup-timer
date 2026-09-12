using ClickUpTimer;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
{
    var bar = new Box(-1920, 1080, 0, 1080 + (int)(48 * scale));
    var busy = new[] { new Box(-1100, bar.Top, -600, bar.Bottom), new Box(-260, bar.Top, 0, bar.Bottom) };
    var result = Placement.Find(bar, busy, scale);
    Check(result is Box b && bar.Contains(b.Left, b.Top) && bar.Contains(b.Right - 1, b.Bottom - 1) && !busy.Any(b.Intersects), $"Fits negative-origin monitor at {scale * 100}% DPI");
}
var normal = new Box(0, 1032, 1920, 1080);
var centered = new[] { new Box(700, 1032, 1300, 1080), new Box(1680, 1032, 1920, 1080) };
Check(Placement.Find(normal, centered, 1)?.Left == 6, "Default uses left gap");
Check(Placement.Find(normal, centered, 1, 200)?.Left == 200, "Restores requested horizontal placement");
var expanded = new[] { new Box(400, 1032, 1700, 1080), new Box(1680, 1032, 1920, 1080) };
Check(Placement.Find(normal, expanded, 1, 300) is Box moved && moved.Right <= 394, "New taskbar icons push timer away safely");
Check(Placement.Find(normal, new[] { normal }, 1) is null, "Full taskbar yields no placement");
Check(Placement.Find(new Box(0, 0, 48, 1080), [], 1) is null, "Unsupported vertical bar does not cover controls");
Check(Placement.Find(new Box(0, 1078, 1920, 1080), [], 1) is null, "Auto-hidden bar is not overlaid");
Check(Placement.Find(normal, centered, 1, -9000)?.Left == 6, "Stale off-screen position returns on-screen");
Check(Placement.Find(new Box(0, 0, 300, 48), [], 1) is null, "Too-small gap never truncates timer");
var verified = new TaskbarSnapshot(1, "monitor1", true, new Box(0, 0, 1920, 1080), new Box(0, 0, 1920, 1032), normal, 1, centered.ToList(), true, null);
var unavailable = verified with { Reliable = false, Occupied = [] };
var recovered = unavailable.PreserveControlsFrom(verified);
Check(recovered.Reliable && recovered.Occupied.SequenceEqual(centered), "Start-menu accessibility interruption retains verified controls");
Check(!unavailable.PreserveControlsFrom(null).Reliable, "No cached geometry does not invent a safe gap");
Check(!(unavailable with { Handle = 2 }).PreserveControlsFrom(verified).Reliable, "Explorer replacement requires rediscovery");
Check(!(unavailable with { WorkArea = verified.Monitor }).PreserveControlsFrom(verified).Reliable, "Auto-hide work area invalidates cache");
Check(!(unavailable with { Scale = 1.5 }).PreserveControlsFrom(verified).Reliable, "DPI change invalidates cache");
Check(!(unavailable with { Device = "monitor2" }).PreserveControlsFrom(verified).Reliable, "Monitor replacement invalidates cache");
Check((verified with { Occupied = [normal] }).PreserveControlsFrom(verified).Occupied.Single() == normal, "Fresh occupied controls override cached free space");
Console.WriteLine($"{checks} checks passed.");
foreach (var scale in new[] { 1.0, 1.5, 2.0 })
{
    var work = new Box(-1920, 0, 0, 1032);
    var floating = Placement.Floating(work, scale, 1, 1);
    Check(work.Contains(floating.Left, floating.Top) && floating.Right == work.Right && floating.Bottom == work.Bottom, $"Floating stays within negative-origin work area at {scale * 100}%");
}
Check(Placement.Floating(new Box(0, 0, 1920, 1032), 1, -10, 20).Left == 0, "Floating stale fractions clamp on-screen");
Console.WriteLine($"{checks} total placement checks passed.");

foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
{
    var bar = new Box(-1920, 1080, 0, 1080 + (int)(48 * scale));
    foreach (var size in new[] { (155.0, 32.0), (260.0, 32.0), (330.0, 40.0), (410.0, 32.0) })
    {
        var placed = Placement.Find(bar, [], scale, null, size.Item1, size.Item2);
        Check(placed is Box b && b.Width == Math.Ceiling(size.Item1 * scale) && b.Height == Math.Ceiling(size.Item2 * scale), $"Presentation footprint {size} scales correctly at {scale}");
        var floating = Placement.Floating(new Box(-1920, 0, 0, 1032), scale, 1, 1, size.Item1, size.Item2);
        Check(floating.Right == 0 && floating.Bottom == 1032 && floating.Width == Math.Ceiling(size.Item1 * scale), "Floating uses the selected presentation dimensions");
    }
}
Check(Placement.Find(new Box(0, 0, 300, 48), [], 1, null, 260, 32) is not null && Placement.Find(new Box(0, 0, 300, 48), [], 1, null, 330, 40) is null, "Compact can fit a gap that rejects Detailed without clipping");
Console.WriteLine($"{checks} total placement checks passed.");
