namespace ShopVerse.Infrastructure.Seed;

internal sealed record BrandSeed(string Name, string Country, string Initials, string Color);

internal sealed record SellerSeed(string StoreName, string City, string Division, string Blurb, string Initials, string Color);

internal sealed record CitySeed(string City, string Division);

/// <summary>Static reference data for the demo marketplace: brands, sellers, locations and customer names.</summary>
internal static class SeedWorld
{
    public static readonly BrandSeed[] Brands =
    [
        new("Siemens", "Germany", "SI", "#009999"), new("Mitsubishi Electric", "Japan", "ME", "#E60012"),
        new("Omron", "Japan", "OM", "#005EB8"), new("Delta Electronics", "Taiwan", "DE", "#0077C8"),
        new("Schneider Electric", "France", "SE", "#3DCD58"), new("ABB", "Switzerland", "AB", "#FF000F"),
        new("Rockwell Automation", "USA", "RA", "#C8102E"), new("Yaskawa", "Japan", "YA", "#0054A6"),
        new("Festo", "Germany", "FE", "#0091DC"), new("SMC", "Japan", "SM", "#0F4C9A"),
        new("Autonics", "South Korea", "AU", "#E2231A"), new("Fuji Electric", "Japan", "FU", "#E60012"),
        new("LS Electric", "South Korea", "LS", "#E4002B"), new("Pepperl+Fuchs", "Germany", "PF", "#FFB400"),
        new("SICK", "Germany", "SK", "#0A4A8C"), new("Phoenix Contact", "Germany", "PC", "#E2001A"),
        new("WAGO", "Germany", "WA", "#E30613"), new("Weidmüller", "Germany", "WM", "#00518F"),
        new("Rittal", "Germany", "RI", "#C6002B"), new("Hager", "Germany", "HA", "#005DAA"),
        new("Eaton", "Ireland", "EA", "#0058A3"), new("Danfoss", "Denmark", "DA", "#E2000F"),
        new("Endress+Hauser", "Switzerland", "EH", "#0066B3"), new("Yokogawa", "Japan", "YO", "#0066B3"),
        new("Grundfos", "Denmark", "GR", "#0072BC"), new("Lenze", "Germany", "LE", "#F39200"),
        new("WEG", "Brazil", "WE", "#0069B4"), new("Havells", "India", "HV", "#E31E24"),
        new("CHINT", "China", "CH", "#E60012"), new("Hanyoung Nux", "South Korea", "HN", "#1E5AA8"),
        new("Moxa", "Taiwan", "MO", "#00A0DF"), new("Advantech", "Taiwan", "AD", "#1E75BB"),
        new("Weintek", "Taiwan", "WT", "#00A651"), new("Fluke", "USA", "FL", "#FFC20E"),
        new("Mean Well", "Taiwan", "MW", "#00B7C3"), new("Camozzi", "Italy", "CA", "#E30613"),
        new("AirTAC", "Taiwan", "AT", "#004A99"), new("Pilz", "Germany", "PZ", "#FFDD00"),
        new("Balluff", "Germany", "BA", "#E2001A"), new("IFM Electronic", "Germany", "IF", "#FFCC00")
    ];

    public static readonly SellerSeed[] Sellers =
    [
        new("MECHWARE Official Store", "Dhaka", "Dhaka", "Official MECHWARE store: authentic automation components with engineering support from our Gulshan design office.", "MW", "#F97316"),
        new("Tejgaon Automation Hub", "Dhaka", "Dhaka", "PLC, HMI and drive specialists supplying Tejgaon industrial area factories since 2011.", "TA", "#0EA5E9"),
        new("Banani Drives & Controls", "Dhaka", "Dhaka", "Authorised drive and motion-control reseller with in-house commissioning engineers.", "BD", "#6366F1"),
        new("Uttara Electro Solutions", "Dhaka", "Dhaka", "Low-voltage switchgear, contactors and control components for panel builders.", "UE", "#14B8A6"),
        new("Narayanganj Industrial Traders", "Narayanganj", "Dhaka", "Textile and dyeing mill spares: sensors, relays, motors and pneumatics.", "NI", "#EF4444"),
        new("Gazipur Factory Mart", "Gazipur", "Dhaka", "One-stop industrial store for Gazipur garment and electronics factories.", "GF", "#8B5CF6"),
        new("Savar Machinery Depot", "Savar", "Dhaka", "Motors, gearboxes and power transmission parts with same-day Savar delivery.", "SM", "#F59E0B"),
        new("Chattogram Power & Control", "Chattogram", "Chattogram", "Port-city distributor for drives, breakers and process instruments.", "CP", "#0284C7"),
        new("Agrabad Engineering Supply", "Chattogram", "Chattogram", "Marine and steel-industry electricals, stocked in Agrabad.", "AE", "#DC2626"),
        new("Khulna Industrial Solutions", "Khulna", "Khulna", "Jute, seafood-processing and cement plant automation supplies for the south-west.", "KI", "#059669"),
        new("Rajshahi Electro House", "Rajshahi", "Rajshahi", "Motor control, sensors and instrumentation for the north-west industrial belt.", "RE", "#7C3AED"),
        new("Sylhet Automation Store", "Sylhet", "Sylhet", "Tea-estate and gas-field automation components with Sylhet-region support.", "SA", "#0891B2"),
        new("Bogura Tech Supply", "Bogura", "Rajshahi", "Poultry feed mill and agro-processing control parts.", "BT", "#65A30D"),
        new("Mymensingh Industrial Mart", "Mymensingh", "Mymensingh", "Pumps, motors and starters for agro-industrial customers.", "MI", "#EA580C"),
        new("Cumilla Control Systems", "Cumilla", "Chattogram", "Control panels, relays and PLC accessories for the Cumilla EPZ cluster.", "CC", "#2563EB"),
        new("Narsingdi Textile Parts", "Narsingdi", "Dhaka", "Spinning and weaving machine electricals: encoders, sensors and drives.", "NT", "#DB2777"),
        new("Tongi Panel Components", "Tongi", "Dhaka", "DIN-rail, enclosure and terminal components for panel assemblers.", "TP", "#9333EA"),
        new("Ashulia Garments Engineering", "Ashulia", "Dhaka", "Sewing-floor and utility-room automation parts for RMG factories.", "AG", "#D97706"),
        new("Kishoreganj Pneumatics & Hydraulics", "Bhairab", "Dhaka", "Cylinders, valves, FRL and fittings in depth.", "KP", "#0D9488"),
        new("Jashore Instrumentation", "Jashore", "Khulna", "Pressure, temperature and flow instruments with calibration service.", "JI", "#4F46E5"),
        new("Barishal Motor House", "Barishal", "Barishal", "Induction motors, geared motors and blowers.", "BM", "#B91C1C"),
        new("Rangpur Industrial Electric", "Rangpur", "Rangpur", "North-Bengal supplier of switchgear and power-quality components.", "RI", "#16A34A"),
        new("Dhaka Sensors & Instruments", "Dhaka", "Dhaka", "Inductive, photoelectric and process sensors from a Motijheel warehouse.", "DS", "#0369A1"),
        new("Mirpur Switchgear Centre", "Dhaka", "Dhaka", "MCB, MCCB, contactor and overload relay stockist.", "MS", "#C2410C"),
        new("Keraniganj Cable & Wire Mart", "Dhaka", "Dhaka", "Control cable, connectors, glands and wiring accessories.", "KC", "#475569")
    ];

    public static readonly CitySeed[] Cities =
    [
        new("Dhaka", "Dhaka"), new("Gazipur", "Dhaka"), new("Narayanganj", "Dhaka"), new("Savar", "Dhaka"),
        new("Ashulia", "Dhaka"), new("Tongi", "Dhaka"), new("Chattogram", "Chattogram"), new("Cumilla", "Chattogram"),
        new("Khulna", "Khulna"), new("Jashore", "Khulna"), new("Rajshahi", "Rajshahi"), new("Bogura", "Rajshahi"),
        new("Sylhet", "Sylhet"), new("Mymensingh", "Mymensingh"), new("Barishal", "Barishal"), new("Rangpur", "Rangpur")
    ];

    public static readonly string[] Industries =
    [
        "garment", "textile dyeing", "pharmaceutical", "food processing", "packaging", "plastic moulding", "cement",
        "steel re-rolling", "ceramic tile", "paper mill", "ETP / water treatment", "poultry feed", "jute mill",
        "dairy", "battery", "spinning", "electronics assembly", "bottling", "chemical", "foam"
    ];

    public static readonly string[] MaleFirst =
    [
        "Abdul", "Rafiqul", "Mizanur", "Anisur", "Shahidul", "Kamrul", "Jahangir", "Mahbub", "Tanvir", "Sabbir",
        "Imran", "Rakibul", "Nazmul", "Habibur", "Sohel", "Faruk", "Masud", "Arif", "Shakil", "Mehedi",
        "Zahid", "Rubel", "Sumon", "Biplob", "Tushar", "Mamun", "Ashraful", "Monir", "Ratan", "Liton",
        "Sajib", "Foysal", "Emon", "Shuvo", "Rajib", "Dipu", "Anik", "Parvez", "Kawser", "Nasir"
    ];

    public static readonly string[] FemaleFirst =
    [
        "Nusrat", "Farhana", "Sharmin", "Rokeya", "Shirin", "Tahmina", "Nasrin", "Sumaiya", "Mitu", "Ruma",
        "Jesmin", "Moushumi", "Taslima", "Salma", "Lipi", "Sadia", "Afsana", "Rehana", "Shapla", "Popy"
    ];

    public static readonly string[] LastNames =
    [
        "Hossain", "Rahman", "Islam", "Ahmed", "Khan", "Chowdhury", "Uddin", "Alam", "Mia", "Sarker",
        "Talukder", "Mondal", "Bhuiyan", "Siddique", "Karim", "Haque", "Majumder", "Biswas", "Das", "Roy",
        "Paul", "Ghosh", "Sheikh", "Molla", "Akter", "Begum", "Khatun", "Sultana", "Parvin", "Jahan"
    ];

    public static readonly string[] Roles =
    [
        "maintenance engineer", "electrical supervisor", "automation engineer", "panel builder", "plant manager",
        "procurement officer", "commissioning engineer", "utility engineer", "workshop owner", "production manager"
    ];
}
