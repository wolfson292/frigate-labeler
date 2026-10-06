namespace FrigateLabeler;

/// <summary>
/// The labels Frigate+ supports, as defined in the plus.frigate.video web app (LABEL_GROUPS,
/// October 2026). A camera only gets annotated for the labels enabled on it in Frigate+; this list
/// is used to point out supported labels a camera hasn't enabled yet.
///
/// Verification in Frigate+ is per label: enabling a new label on a camera makes every existing
/// image from that camera unverified for that label only. Those images then need labeling for just
/// the new label; the boxes for labels they're already verified for stay as they are.
/// </summary>
public static class FrigateLabels
{
    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>
    {
        ["people"] = ["person", "baby", "face"],
        ["vehicles"] = ["car", "motorcycle", "school_bus", "garbage_truck", "bicycle", "boat", "license_plate"],
        ["logos"] = ["amazon", "usps", "fedex", "ups", "dhl", "an_post", "purolator", "postnl", "nzpost", "postnord",
            "gls", "dpd", "royal_mail", "canada_post"],
        ["animals"] = ["dog", "cat", "deer", "horse", "bird", "raccoon", "fox", "bear", "cow", "squirrel", "goat",
            "rabbit", "skunk", "kangaroo", "possum", "rodent"],
        ["other"] = ["package", "waste_bin", "bbq_grill", "robot_lawnmower", "baby_stroller", "umbrella"],
    };

    public static readonly IReadOnlyList<string> Supported = Groups.Values.SelectMany(l => l).Distinct().ToList();

    public static List<string> NotEnabled(IEnumerable<string> cameraLabels) =>
        Supported.Except(cameraLabels).ToList();

    /// <summary>The labels an image still needs: enabled on its camera but not yet verified on it.</summary>
    public static List<string> Unverified(IEnumerable<string> cameraLabels, IEnumerable<string> verifiedLabels) =>
        cameraLabels.Except(verifiedLabels).ToList();
}
