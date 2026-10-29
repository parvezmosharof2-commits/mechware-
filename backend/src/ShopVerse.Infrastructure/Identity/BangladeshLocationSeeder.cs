using ShopVerse.Domain.Identity;

namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// Pre-seeded Bangladesh administrative hierarchy (8 Divisions, major Industrial Districts, and Upazilas)
/// with bilingual English & Bangla names and shipping zone classification.
/// </summary>
public static class BangladeshLocationSeeder
{
    public static readonly Guid DivDhakaId = Guid.Parse("11111111-0001-0000-0000-000000000001");
    public static readonly Guid DivChattogramId = Guid.Parse("11111111-0001-0000-0000-000000000002");
    public static readonly Guid DivRajshahiId = Guid.Parse("11111111-0001-0000-0000-000000000003");
    public static readonly Guid DivKhulnaId = Guid.Parse("11111111-0001-0000-0000-000000000004");
    public static readonly Guid DivSylhetId = Guid.Parse("11111111-0001-0000-0000-000000000005");
    public static readonly Guid DivRangpurId = Guid.Parse("11111111-0001-0000-0000-000000000006");
    public static readonly Guid DivBarishalId = Guid.Parse("11111111-0001-0000-0000-000000000007");
    public static readonly Guid DivMymensinghId = Guid.Parse("11111111-0001-0000-0000-000000000008");

    public static readonly Guid DistDhakaId = Guid.Parse("22222222-0001-0000-0000-000000000001");
    public static readonly Guid DistGazipurId = Guid.Parse("22222222-0001-0000-0000-000000000002");
    public static readonly Guid DistNarayanganjId = Guid.Parse("22222222-0001-0000-0000-000000000003");
    public static readonly Guid DistChattogramId = Guid.Parse("22222222-0002-0000-0000-000000000001");
    public static readonly Guid DistPabnaId = Guid.Parse("22222222-0003-0000-0000-000000000001");
    public static readonly Guid DistRajshahiId = Guid.Parse("22222222-0003-0000-0000-000000000002");
    public static readonly Guid DistKhulnaId = Guid.Parse("22222222-0004-0000-0000-000000000001");
    public static readonly Guid DistSylhetId = Guid.Parse("22222222-0005-0000-0000-000000000001");

    public static IReadOnlyList<BdLocation> GetSeedLocations() =>
    [
        // Divisions
        new(DivDhakaId, null, "division", "Dhaka", "ঢাকা", "inside_dhaka", "1000"),
        new(DivChattogramId, null, "division", "Chattogram", "চট্টগ্রাম", "outside_dhaka", "4000"),
        new(DivRajshahiId, null, "division", "Rajshahi", "রাজশাহী", "outside_dhaka", "6000"),
        new(DivKhulnaId, null, "division", "Khulna", "খুলনা", "outside_dhaka", "9000"),
        new(DivSylhetId, null, "division", "Sylhet", "সিলেট", "outside_dhaka", "3100"),
        new(DivRangpurId, null, "division", "Rangpur", "রংপুর", "outside_dhaka", "5400"),
        new(DivBarishalId, null, "division", "Barishal", "বরিশাল", "outside_dhaka", "8200"),
        new(DivMymensinghId, null, "division", "Mymensingh", "ময়মনসিংহ", "outside_dhaka", "2200"),

        // Districts
        new(DistDhakaId, DivDhakaId, "district", "Dhaka", "ঢাকা", "inside_dhaka", "1200"),
        new(DistGazipurId, DivDhakaId, "district", "Gazipur", "গাজীপুর", "dhaka_suburb", "1700"),
        new(DistNarayanganjId, DivDhakaId, "district", "Narayanganj", "নারায়ণগঞ্জ", "dhaka_suburb", "1400"),
        new(DistChattogramId, DivChattogramId, "district", "Chattogram", "চট্টগ্রাম", "outside_dhaka", "4000"),
        new(DistPabnaId, DivRajshahiId, "district", "Pabna", "পাবনা", "outside_dhaka", "6600"),
        new(DistRajshahiId, DivRajshahiId, "district", "Rajshahi", "রাজশাহী", "outside_dhaka", "6000"),
        new(DistKhulnaId, DivKhulnaId, "district", "Khulna", "খুলনা", "outside_dhaka", "9000"),
        new(DistSylhetId, DivSylhetId, "district", "Sylhet", "সিলেট", "outside_dhaka", "3100"),

        // Upazilas / Industrial Zones
        new(Guid.Parse("33333333-0001-0001-0000-000000000001"), DistDhakaId, "upazila", "Tejgaon Industrial Area", "তেজগাঁও শিল্পাঞ্চল", "inside_dhaka", "1208"),
        new(Guid.Parse("33333333-0001-0001-0000-000000000002"), DistDhakaId, "upazila", "Gulshan", "গুলশান", "inside_dhaka", "1212"),
        new(Guid.Parse("33333333-0001-0001-0000-000000000003"), DistDhakaId, "upazila", "Uttara", "উত্তরা", "inside_dhaka", "1230"),
        new(Guid.Parse("33333333-0001-0001-0000-000000000004"), DistDhakaId, "upazila", "Savar EPZ", "সাভার ইপিজেড", "dhaka_suburb", "1349"),
        new(Guid.Parse("33333333-0001-0002-0000-000000000001"), DistGazipurId, "upazila", "Tongi Industrial Zone", "টঙ্গী শিল্পাঞ্চল", "dhaka_suburb", "1710"),
        new(Guid.Parse("33333333-0001-0002-0000-000000000002"), DistGazipurId, "upazila", "Gazipur Sadar", "গাজীপুর সদর", "dhaka_suburb", "1700"),
        new(Guid.Parse("33333333-0001-0003-0000-000000000001"), DistNarayanganjId, "upazila", "Siddhirganj Adamjee EPZ", "সিদ্ধিরগঞ্জ আদমজী ইপিজেড", "dhaka_suburb", "1431"),
        new(Guid.Parse("33333333-0002-0001-0000-000000000001"), DistChattogramId, "upazila", "Chattogram EPZ (Halishahar)", "চট্টগ্রাম ইপিজেড (হালিশহর)", "outside_dhaka", "4223"),
        new(Guid.Parse("33333333-0002-0001-0000-000000000002"), DistChattogramId, "upazila", "Sitakunda Shipbreaking & Steel Zone", "সীতাকুণ্ড শিল্পাঞ্চল", "outside_dhaka", "4310"),
        new(Guid.Parse("33333333-0003-0001-0000-000000000001"), DistPabnaId, "upazila", "Pabna Sadar (BSCIC)", "পাবনা সদর (বিসিক)", "outside_dhaka", "6600"),
        new(Guid.Parse("33333333-0003-0001-0000-000000000002"), DistPabnaId, "upazila", "Ishwardi EPZ", "ঈশ্বরদী ইপিজেড", "outside_dhaka", "6620")
    ];
}
