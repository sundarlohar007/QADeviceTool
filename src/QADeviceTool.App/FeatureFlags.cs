namespace LogPro;

/// <summary>
/// Feature flags for incremental rollout of new features.
/// All features default to disabled (false).
/// </summary>
public static class FeatureFlags
{
    /// <summary>
    /// Enable AI-powered log analysis and anomaly detection.
    /// </summary>
    public static bool AiLogAnalysis { get; set; } = false;

    /// <summary>
    /// Enable multi-device selection and batch operations.
    /// </summary>
    public static bool MultiSelect { get; set; } = false;
}
