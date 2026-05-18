namespace LogPro.Models;

public class ScrcpyOptions
{
    public string BitRate { get; set; } = "2M";
    public int MaxFps { get; set; } = 60;
    public string WindowPreset { get; set; } = "Default";
    public int WindowX { get; set; } = 0;
    public int WindowY { get; set; } = 0;
    public int WindowW { get; set; } = 0;
    public int WindowH { get; set; } = 0;
    public bool Fullscreen { get; set; } = false;
}