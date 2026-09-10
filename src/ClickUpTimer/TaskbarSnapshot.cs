namespace ClickUpTimer;

internal sealed record TaskbarSnapshot(long Handle, string Device, bool Primary, Box Monitor, Box WorkArea,
    Box Bounds, double Scale, List<Box> Occupied, bool Reliable, string? Error)
{
    internal bool HasSameReservedGeometry(TaskbarSnapshot other) =>
        Handle == other.Handle && Device == other.Device && Monitor == other.Monitor &&
        WorkArea == other.WorkArea && Bounds == other.Bounds && Scale == other.Scale &&
        Bounds.Width > 0 && Bounds.Height > 0 && !Bounds.Intersects(WorkArea);

    internal TaskbarSnapshot PreserveControlsFrom(TaskbarSnapshot? previous) =>
        !Reliable && previous is { Reliable: true } && HasSameReservedGeometry(previous)
            ? this with { Occupied = previous.Occupied, Reliable = true, Error = "Using last verified controls while shell accessibility is unavailable" }
            : this;
}
