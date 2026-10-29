namespace ShopVerse.Infrastructure.Seed;

/// <summary>Roots 4–6: sensors & switches, motors & gearboxes, power distribution & protection.</summary>
internal static class FamiliesSensorsPower
{
    public static IEnumerable<Family> All()
    {
        // ───────────────────────── 4. Sensors & Switches ─────────────────────────
        yield return new Family
        {
            Leaf = "inductive-proximity-sensors",
            Brand = "Autonics",
            Series = "PR Series",
            NounEn = "Inductive Proximity Sensor",
            NounBn = "ইন্ডাক্টিভ প্রক্সিমিটি সেন্সর",
            Header = "Model|Thread|Range|Output|Price",
            Rows =
            [
                "PR08-2DN|M8|2 mm|NPN NO|1450",
                "PR12-4DN|M12|4 mm|NPN NO|1250",
                "PR12-4DP|M12|4 mm|PNP NO|1250",
                "PR18-5DN|M18|5 mm|NPN NO|1450",
                "PR18-5DP|M18|5 mm|PNP NO|1450",
                "PR30-10DN|M30|10 mm|NPN NO|1950",
                "PR30-10DP|M30|10 mm|PNP NO|1950"
            ],
            Title = "{Brand} {Model} Inductive Proximity Sensor {Thread}, {Range} Sensing, {Output}",
            Intro = "The {Model} is a cylindrical shielded inductive proximity sensor, {Thread} thread with {Range} sensing distance and {Output} output at 12–24 V DC. The standard 2 m PVC cable and bright indicator LED suit general machine automation.",
            Highlights = ["{Thread} body, {Range} sensing distance", "{Output} output, 12–24 V DC", "Shielded (flush) mounting", "IP67, 2 m PVC cable"],
            Applications = ["Detecting metal parts on conveyors", "Cylinder end-of-stroke detection", "Counting and speed pick-up"],
            Specs = ["General:Model=Model", "Sensing:Thread=Body thread;Range=Sensing distance", "Electrical:Output=Output type"],
            Fixed = ["Electrical:Supply voltage=12–24 V DC", "Electrical:Response frequency=1–1.5 kHz", "Environment:Degree of protection=IP67", "General:Cable=2 m PVC, 3-wire"],
            Shape = Shape.RoundSensor,
            WeightKg = 0.08m,
            BoxCm = (9m, 4m, 4m),
            Popularity = 1.6,
            Axes = ["Connection|2 m cable:1.00;M12 connector:1.12"],
            FilterSpecs = ["Body thread", "Output type"]
        };

        yield return new Family
        {
            Leaf = "inductive-proximity-sensors",
            Brand = "Pepperl+Fuchs",
            Series = "NBB",
            NounEn = "Inductive Sensor",
            NounBn = "ইন্ডাক্টিভ সেন্সর",
            Header = "Model|Thread|Range|Price",
            Rows =
            [
                "NBB2-12GM40-E2|M12|2 mm (flush)|3850",
                "NBB4-12GM50-E2|M12|4 mm (flush)|3950",
                "NBB5-18GM50-E2|M18|5 mm (flush)|4250",
                "NBB8-18GM50-E2|M18|8 mm (non-flush)|4650",
                "NBB10-30GM50-E2|M30|10 mm (flush)|5450",
                "NBB15-30GM50-E2|M30|15 mm (non-flush)|5950"
            ],
            Title = "{Brand} {Model} Inductive Sensor {Thread}, {Range}",
            Intro = "The Pepperl+Fuchs {Model} is a two-wire-switching inductive sensor with {Range} sensing range in a {Thread} housing. The NAMUR-style construction and robust nickel-plated brass body make it a long-life choice for harsh process and heavy-industry environments.",
            Highlights = ["{Thread} housing, {Range}", "NBB industrial series with robust brass body", "Short-circuit and reverse-polarity protection", "IP67"],
            Applications = ["Heavy-industry position detection", "Machine tools", "Process valves"],
            Specs = ["General:Model=Model", "Sensing:Thread=Body thread;Range=Sensing range"],
            Fixed = ["Electrical:Supply voltage=10–30 V DC", "Environment:Degree of protection=IP67", "Environment:Operating temperature=-25 to 70 °C"],
            Shape = Shape.RoundSensor,
            WeightKg = 0.1m,
            BoxCm = (10m, 4m, 4m),
            Popularity = 0.9,
            FilterSpecs = ["Body thread"]
        };

        yield return new Family
        {
            Leaf = "capacitive-proximity-sensors",
            Brand = "Autonics",
            Series = "CR Series",
            NounEn = "Capacitive Proximity Sensor",
            NounBn = "ক্যাপাসিটিভ প্রক্সিমিটি সেন্সর",
            Header = "Model|Thread|Range|Output|Price",
            Rows =
            [
                "CR18-8DN|M18|8 mm|NPN NO|2850",
                "CR18-8DP|M18|8 mm|PNP NO|2850",
                "CR30-15DN|M30|15 mm|NPN NO|3450",
                "CR30-15DP|M30|15 mm|PNP NO|3450"
            ],
            Title = "{Brand} {Model} Capacitive Proximity Sensor {Thread}, {Range}, {Output}",
            Intro = "The {Model} detects non-metallic materials — plastics, glass, liquids, grain and powders — through container walls with {Range} sensing distance. A sensitivity potentiometer allows adjustment for different media.",
            Highlights = ["{Thread}, {Range} sensing", "{Output}, 10–30 V DC", "Sensitivity potentiometer", "IP66"],
            Applications = ["Level detection through tank walls", "Grain and powder hopper monitoring", "Plastics and glass detection"],
            Specs = ["General:Model=Model", "Sensing:Thread=Body thread;Range=Sensing distance", "Electrical:Output=Output type"],
            Fixed = ["Electrical:Supply voltage=10–30 V DC", "Environment:Degree of protection=IP66"],
            Shape = Shape.RoundSensor,
            WeightKg = 0.1m,
            BoxCm = (10m, 5m, 5m),
            Popularity = 0.7
        };

        yield return new Family
        {
            Leaf = "diffuse-retroreflective-sensors",
            Brand = "Omron",
            Series = "E3Z",
            NounEn = "Photoelectric Sensor",
            NounBn = "ফটোইলেকট্রিক সেন্সর",
            Header = "Model|Type|Range|Output|Price",
            Rows =
            [
                "E3Z-D62|Diffuse-reflective|100 mm (adjustable)|NPN|4250",
                "E3Z-D82|Diffuse-reflective|100 mm (adjustable)|PNP|4250",
                "E3Z-R61|Retro-reflective (with polariser)|4 m|NPN|4850",
                "E3Z-R81|Retro-reflective (with polariser)|4 m|PNP|4850"
            ],
            Title = "{Brand} {Model} {Type} Photoelectric Sensor, {Range}, {Output}",
            Intro = "The Omron {Model} is a compact {Type} photoelectric sensor with {Range} sensing range and {Output} output. The sensitivity adjuster and visible-light LED make installation simple on conveyors and packaging lines.",
            Highlights = ["{Type}, range {Range}", "{Output} output, 12–24 V DC", "Light-ON / Dark-ON selectable", "IP67, 2 m cable"],
            Applications = ["Box and product detection on conveyors", "Bottle and pouch counting", "Presence detection on packaging machines"],
            Specs = ["General:Model=Model;Type=Sensing method", "Sensing:Range=Sensing distance", "Electrical:Output=Output"],
            Fixed = ["Electrical:Supply voltage=12–24 V DC", "Environment:Degree of protection=IP67", "General:Cable=2 m PVC"],
            Shape = Shape.BlockSensor,
            WeightKg = 0.09m,
            BoxCm = (9m, 6m, 4m),
            Popularity = 1.3
        };

        yield return new Family
        {
            Leaf = "diffuse-retroreflective-sensors",
            Brand = "Autonics",
            Series = "BR Series",
            NounEn = "Photoelectric Sensor",
            NounBn = "ফটোইলেকট্রিক সেন্সর",
            Header = "Model|Type|Range|Price",
            Rows =
            [
                "BR100-DDT|Diffuse-reflective|100 mm|3450",
                "BR400-DDT|Diffuse-reflective|400 mm|3850",
                "BR3M-MDT|Retro-reflective|3 m|3650"
            ],
            Title = "{Brand} {Model} {Type} Photoelectric Sensor {Range}",
            Intro = "The Autonics {Model} is a general-purpose {Type} sensor with {Range} range. The PNP/NPN-selectable output and 10–30 V DC supply cover most machine-builder requirements.",
            Highlights = ["{Type}, {Range} range", "Selectable NPN / PNP output", "Built-in dark/light ON switch", "IP66"],
            Applications = ["Conveyor object detection", "Sorting and counting"],
            Specs = ["General:Model=Model;Type=Sensing method", "Sensing:Range=Sensing distance"],
            Fixed = ["Electrical:Supply voltage=10–30 V DC", "Environment:Degree of protection=IP66"],
            Shape = Shape.BlockSensor,
            WeightKg = 0.07m,
            BoxCm = (9m, 6m, 4m),
            Popularity = 0.9
        };

        yield return new Family
        {
            Leaf = "fibre-through-beam-sensors",
            Brand = "Omron",
            Series = "E3Z / E3X",
            NounEn = "Photoelectric Sensor",
            NounBn = "ফটোইলেকট্রিক সেন্সর",
            Header = "Model|Type|Range|Output|Price",
            Rows =
            [
                "E3Z-T61|Through-beam (emitter + receiver)|15 m|NPN|5450",
                "E3Z-T81|Through-beam (emitter + receiver)|15 m|PNP|5450",
                "E3X-NA11|Fibre amplifier (cable type)|Fibre dependent|NPN|9250",
                "E3X-NA41|Fibre amplifier (cable type)|Fibre dependent|PNP|9250"
            ],
            Title = "{Brand} {Model} {Type} — {Range}, {Output}",
            Intro = "The Omron {Model} provides {Type} sensing with {Range}. Through-beam sensors give the longest, most reliable detection distance; the E3X amplifier works with a wide range of fibre heads for small-part and tight-space detection.",
            Highlights = ["{Type}", "Range: {Range}", "{Output} output", "Easy alignment indicators"],
            Applications = ["Long-range object detection", "Small-part detection with fibre heads", "Glass and film detection"],
            Specs = ["General:Model=Model;Type=Type", "Sensing:Range=Range", "Electrical:Output=Output"],
            Fixed = ["Electrical:Supply voltage=12–24 V DC", "Environment:Degree of protection=IP66"],
            Shape = Shape.BlockSensor,
            WeightKg = 0.12m,
            BoxCm = (10m, 7m, 5m),
            Popularity = 0.7
        };

        yield return new Family
        {
            Leaf = "limit-switches",
            Brand = "Schneider Electric",
            Series = "OsiSense XC",
            NounEn = "Limit Switch",
            NounBn = "লিমিট সুইচ",
            Header = "Model|Head|Body|Price",
            Rows =
            [
                "XCKP2118P16|Roller lever, thermoplastic roller|Plastic, 1 NC + 1 NO|3250",
                "XCKP2121P16|Steel roller plunger|Plastic, 1 NC + 1 NO|3450",
                "XCKN2118P20|Roller lever, thermoplastic roller|Compact plastic, 1 NC + 1 NO|2650",
                "XCKN2121P20|Steel roller plunger|Compact plastic, 1 NC + 1 NO|2850",
                "XCKJ161|Steel roller plunger|Metal, 1 NC + 1 NO|5850",
                "XCKJ10511|Adjustable roller lever|Metal, 1 NC + 1 NO|6250"
            ],
            Title = "{Brand} {Model} OsiSense Limit Switch — {Head}",
            Intro = "The {Model} is an electromechanical limit switch with a {Head} and a {Body} body. Snap-action contacts and a rugged enclosure make it a dependable end-of-travel switch on cranes, doors and machine guards.",
            Highlights = ["{Head}", "Body: {Body}", "Snap-action contacts, 240 V AC / 3 A", "IP65 / IP66"],
            Applications = ["Crane and hoist end-of-travel", "Machine guard doors", "Conveyor stops"],
            Specs = ["General:Model=Model", "Mechanical:Head=Operating head;Body=Body and contacts"],
            Fixed = ["Environment:Degree of protection=IP65 / IP66", "Electrical:Rated current=3 A at 240 V AC"],
            Shape = Shape.BlockSensor,
            WeightKg = 0.2m,
            BoxCm = (10m, 8m, 6m),
            Popularity = 1.0
        };

        yield return new Family
        {
            Leaf = "rotary-encoders",
            Brand = "Omron",
            Series = "E6B2-C",
            NounEn = "Incremental Rotary Encoder",
            NounBn = "ইনক্রিমেন্টাল রোটারি এনকোডার",
            Header = "Model|Output|Supply|Price",
            Rows =
            [
                "E6B2-CWZ6C|NPN open collector (A, B, Z)|5–24 V DC|8450",
                "E6B2-CWZ3E|NPN voltage output (A, B, Z)|5–12 V DC|8450",
                "E6B2-CWZ1X|Line driver (A, B, Z)|5–24 V DC|9650"
            ],
            Title = "{Brand} {Model} Incremental Rotary Encoder Ø40 mm, {Output}",
            Intro = "The Omron {Model} is a Ø40 mm incremental rotary encoder with Ø6 mm shaft and 0.5 m cable. It is used on conveyors, cutters and spinning machinery for length measurement and speed feedback; choose the pulses-per-revolution you need from the options.",
            Highlights = ["Ø40 mm body, Ø6 mm shaft", "Output: {Output}", "Supply {Supply}", "Resolution selectable from 100 to 2 000 P/R"],
            Applications = ["Length measurement on cutters", "Speed feedback", "Positioning on conveyors"],
            Specs = ["General:Model=Model", "Electrical:Output=Output;Supply=Supply"],
            Fixed = ["Mechanical:Shaft=Ø6 mm solid", "Environment:Degree of protection=IP50 (IP64 option)"],
            Shape = Shape.Motor,
            WeightKg = 0.12m,
            BoxCm = (10m, 6m, 6m),
            Popularity = 0.8,
            Axes = ["Resolution|100 P/R:1.00;360 P/R:1.00;600 P/R:1.02;1000 P/R:1.04;2000 P/R:1.12"]
        };

        yield return new Family
        {
            Leaf = "rotary-encoders",
            Brand = "Autonics",
            Series = "E40S Series",
            NounEn = "Incremental Rotary Encoder",
            NounBn = "ইনক্রিমেন্টাল রোটারি এনকোডার",
            Header = "Model|Shaft|Output|Price",
            Rows =
            [
                "E40S6-1000-3-T-24|Ø6 mm|Totem pole (A, B, Z)|7650",
                "E40S6-1000-3-N-24|Ø6 mm|NPN open collector|7450",
                "E40H8-1000-3-T-24|Ø8 mm hollow shaft|Totem pole|8650"
            ],
            Title = "{Brand} {Model} Ø40 mm Rotary Encoder, {Shaft}, {Output}",
            Intro = "The Autonics {Model} is an economical Ø40 mm incremental encoder with {Shaft}. Wide pulse range, 5–24 V DC supply and 3-phase output make it suitable for most positioning and speed-feedback tasks.",
            Highlights = ["{Shaft}, Ø40 mm body", "Output: {Output}", "5–24 V DC supply", "Resolution selectable"],
            Applications = ["Length measurement", "Speed feedback", "Textile machinery"],
            Specs = ["General:Model=Model", "Mechanical:Shaft=Shaft", "Electrical:Output=Output"],
            Fixed = ["Environment:Degree of protection=IP50"],
            Shape = Shape.Motor,
            WeightKg = 0.1m,
            BoxCm = (10m, 6m, 6m),
            Popularity = 0.9,
            Axes = ["Resolution|100 P/R:1.00;500 P/R:1.01;1000 P/R:1.03;2000 P/R:1.10"]
        };

        // ───────────────────────── 5. Motors & Gearboxes ─────────────────────────
        yield return new Family
        {
            Leaf = "three-phase-induction-motors",
            Brand = "WEG",
            Series = "W22 IE3",
            NounEn = "Three-phase Induction Motor",
            NounBn = "থ্রি-ফেজ ইন্ডাকশন মোটর",
            Header = "Model|Power|Frame|Speed|Efficiency|Price",
            Rows =
            [
                "W22 IE3 0.75 kW 4P|0.75 kW (1 HP)|80|1 410 rpm|82.5 %|13500",
                "W22 IE3 1.5 kW 4P|1.5 kW (2 HP)|90S|1 430 rpm|85.3 %|18500",
                "W22 IE3 2.2 kW 4P|2.2 kW (3 HP)|100L|1 440 rpm|86.7 %|23500",
                "W22 IE3 3 kW 4P|3 kW (4 HP)|100L|1 440 rpm|87.7 %|28500",
                "W22 IE3 5.5 kW 4P|5.5 kW (7.5 HP)|132S|1 450 rpm|89.6 %|42500",
                "W22 IE3 7.5 kW 4P|7.5 kW (10 HP)|132M|1 455 rpm|90.4 %|54500",
                "W22 IE3 11 kW 4P|11 kW (15 HP)|160M|1 460 rpm|91.4 %|78500",
                "W22 IE3 15 kW 4P|15 kW (20 HP)|160L|1 465 rpm|92.1 %|98500",
                "W22 IE3 22 kW 4P|22 kW (30 HP)|180L|1 465 rpm|93.0 %|145000"
            ],
            Title = "{Brand} {Model} Three-phase Induction Motor, {Power}, Frame {Frame}",
            Intro = "The WEG {Model} is a premium-efficiency (IE3) three-phase squirrel-cage induction motor rated {Power} at 380–415 V / 50 Hz, IEC frame {Frame} with a cast-iron housing. IE3 efficiency of {Efficiency} lowers running cost on continuous-duty pumps, fans and compressors.",
            Highlights = ["{Power}, 4-pole, {Speed}", "IE3 premium efficiency: {Efficiency}", "IEC frame {Frame}, B3 foot mounting", "IP55, Class F insulation, S1 duty"],
            Applications = ["Pumps and compressors", "Fans and blowers", "Conveyors and mixers"],
            Specs = ["General:Model=Model", "Electrical:Power=Rated power;Speed=Rated speed;Efficiency=Efficiency (IE3)", "Mechanical:Frame=IEC frame"],
            Fixed = ["Electrical:Voltage / frequency=380–415 V, 50 Hz", "Electrical:Poles=4", "Environment:Degree of protection=IP55", "Environment:Insulation class=Class F", "Mechanical:Mounting=B3 (foot)"],
            Shape = Shape.Motor,
            WeightKg = 14m,
            BoxCm = (40m, 28m, 28m),
            Popularity = 1.4,
            WarrantyMonths = 24,
            FilterSpecs = ["Rated power", "Efficiency (IE3)"]
        };

        yield return new Family
        {
            Leaf = "single-phase-motors",
            Brand = "Havells",
            Series = "Single-phase Capacitor Motor",
            NounEn = "Single-phase Motor",
            NounBn = "সিঙ্গেল-ফেজ মোটর",
            Header = "Model|Power|Frame|Speed|Price",
            Rows =
            [
                "0.37 kW 2P 2 800 rpm|0.37 kW (½ HP)|71|2 800 rpm|6850",
                "0.55 kW 2P 2 800 rpm|0.55 kW (¾ HP)|71|2 800 rpm|7850",
                "0.75 kW 2P 2 800 rpm|0.75 kW (1 HP)|80|2 800 rpm|9450",
                "1.1 kW 2P 2 800 rpm|1.1 kW (1.5 HP)|80|2 800 rpm|11500",
                "1.5 kW 2P 2 800 rpm|1.5 kW (2 HP)|90S|2 800 rpm|14500",
                "2.2 kW 2P 2 800 rpm|2.2 kW (3 HP)|90L|2 800 rpm|18500"
            ],
            Title = "{Brand} {Model} Single-phase Capacitor-run Motor {Power}",
            Intro = "A 230 V / 50 Hz single-phase capacitor motor rated {Power}, IEC frame {Frame}, {Speed}. Suitable for pumps, compressors, small machine tools and workshop equipment where only a single-phase supply is available.",
            Highlights = ["{Power}, {Speed}, 230 V 50 Hz", "IEC frame {Frame}, B3 foot mounting", "Aluminium housing, ball bearings", "IP44"],
            Applications = ["Small pumps and compressors", "Workshop machinery", "Fans and blowers"],
            Specs = ["General:Model=Model", "Electrical:Power=Rated power;Speed=Rated speed", "Mechanical:Frame=IEC frame"],
            Fixed = ["Electrical:Voltage / frequency=230 V, 50 Hz", "Environment:Degree of protection=IP44", "Mechanical:Mounting=B3 (foot)"],
            Shape = Shape.Motor,
            WeightKg = 7m,
            BoxCm = (32m, 22m, 22m),
            Popularity = 1.0,
            FilterSpecs = ["Rated power"]
        };

        yield return new Family
        {
            Leaf = "helical-geared-motors",
            Brand = "WEG",
            Series = "WG20 Helical",
            NounEn = "Helical Geared Motor",
            NounBn = "হেলিক্যাল গিয়ারড মোটর",
            Header = "Model|Power|Ratio|OutputSpeed|Torque|Price",
            Rows =
            [
                "WG20 0.37 kW i=15|0.37 kW|15:1|93 rpm|35 N·m|19500",
                "WG20 0.55 kW i=20|0.55 kW|20:1|70 rpm|62 N·m|24500",
                "WG20 0.75 kW i=25|0.75 kW|25:1|56 rpm|93 N·m|28500",
                "WG20 1.5 kW i=30|1.5 kW|30:1|47 rpm|190 N·m|42500",
                "WG20 2.2 kW i=40|2.2 kW|40:1|35 rpm|345 N·m|58500",
                "WG20 3 kW i=50|3 kW|50:1|28 rpm|560 N·m|74500"
            ],
            Title = "{Brand} {Model} Helical Geared Motor {Power}, Ratio {Ratio}, {OutputSpeed}",
            Intro = "A foot-mounted helical geared motor combining a {Power} four-pole IE2 motor with a hardened helical gear stage. Ratio {Ratio} gives {OutputSpeed} and about {Torque} output torque, suited to conveyors, mixers and feeders.",
            Highlights = ["{Power}, ratio {Ratio}", "Output speed {OutputSpeed}, torque {Torque}", "Hardened and ground helical gears", "Pre-filled synthetic oil, IP55"],
            Applications = ["Conveyors", "Mixers and agitators", "Feeders and hoppers"],
            Specs = ["General:Model=Model", "Mechanical:Power=Motor power;Ratio=Gear ratio;OutputSpeed=Output speed;Torque=Output torque"],
            Fixed = ["Electrical:Voltage / frequency=380–415 V, 50 Hz", "Environment:Degree of protection=IP55", "Mechanical:Gear type=Helical, 2-stage"],
            Shape = Shape.Motor,
            WeightKg = 24m,
            BoxCm = (48m, 30m, 28m),
            Popularity = 0.8,
            WarrantyMonths = 18,
            FilterSpecs = ["Motor power", "Gear ratio"]
        };

        yield return new Family
        {
            Leaf = "worm-geared-motors",
            Brand = "Lenze",
            Series = "GST Worm Geared Motor",
            NounEn = "Worm Geared Motor",
            NounBn = "ওয়ার্ম গিয়ারড মোটর",
            Header = "Model|Power|Ratio|OutputSpeed|Price",
            Rows =
            [
                "GST03 0.18 kW i=30|0.18 kW|30:1|47 rpm|32500",
                "GST04 0.37 kW i=30|0.37 kW|30:1|47 rpm|42500",
                "GST04 0.37 kW i=60|0.37 kW|60:1|23 rpm|42500",
                "GST05 0.75 kW i=40|0.75 kW|40:1|35 rpm|62500",
                "GST05 1.1 kW i=30|1.1 kW|30:1|47 rpm|72500"
            ],
            Title = "{Brand} {Model} Worm Geared Motor, {Power}, {Ratio}",
            Intro = "A compact right-angle worm geared motor delivering {OutputSpeed} at {Ratio}. The self-locking tendency of the worm stage at higher ratios helps hold loads, making it popular on hoists, gates and conveyor drives.",
            Highlights = ["{Power}, ratio {Ratio}", "Output speed {OutputSpeed}", "Right-angle, hollow or solid output shaft", "Aluminium housing"],
            Applications = ["Conveyors", "Gates and barriers", "Mixers"],
            Specs = ["General:Model=Model", "Mechanical:Power=Motor power;Ratio=Gear ratio;OutputSpeed=Output speed"],
            Fixed = ["Electrical:Voltage / frequency=230/400 V, 50 Hz", "Environment:Degree of protection=IP55"],
            Shape = Shape.Motor,
            WeightKg = 9m,
            BoxCm = (34m, 24m, 24m),
            Popularity = 0.6,
            WarrantyMonths = 18
        };

        yield return new Family
        {
            Leaf = "axial-cooling-fans",
            Brand = "Delta Electronics",
            Series = "AFB DC Axial Fan",
            NounEn = "DC Axial Fan",
            NounBn = "ডিসি অ্যাক্সিয়াল ফ্যান",
            Header = "Model|Size|Voltage|Airflow|Price",
            Rows =
            [
                "60 × 25 mm 12 V|60 × 60 × 25 mm|12 V DC|1.0 m³/min|520",
                "80 × 25 mm 12 V|80 × 80 × 25 mm|12 V DC|1.8 m³/min|580",
                "92 × 25 mm 24 V|92 × 92 × 25 mm|24 V DC|2.4 m³/min|690",
                "120 × 25 mm 24 V|120 × 120 × 25 mm|24 V DC|3.9 m³/min|850",
                "120 × 38 mm 24 V|120 × 120 × 38 mm|24 V DC|5.6 m³/min|1350"
            ],
            Title = "{Brand} AFB-series DC Axial Fan {Size}, {Voltage}",
            Intro = "A ball-bearing DC axial fan of {Size} for control-panel, VFD and power-supply cooling, moving {Airflow} at {Voltage}. Dual-ball bearings give long life at elevated temperatures.",
            Highlights = ["{Size}, {Voltage}", "Airflow {Airflow}", "Dual ball bearings", "Tacho / PWM options available"],
            Applications = ["Panel and drive cooling", "Power-supply cooling", "Equipment ventilation"],
            Specs = ["General:Model=Model", "Mechanical:Size=Dimensions", "Electrical:Voltage=Rated voltage;Airflow=Airflow"],
            Fixed = ["Environment:Operating temperature=-10 to 70 °C"],
            Shape = Shape.Cylinder,
            WeightKg = 0.08m,
            BoxCm = (14m, 14m, 5m),
            Popularity = 1.0
        };

        yield return new Family
        {
            Leaf = "centrifugal-blowers",
            Brand = "Delta Electronics",
            Series = "BFB Blower",
            NounEn = "DC Centrifugal Blower",
            NounBn = "ডিসি সেন্ট্রিফিউগাল ব্লোয়ার",
            Header = "Model|Size|Voltage|Airflow|Price",
            Rows =
            [
                "75 × 30 mm 12 V|75 × 75 × 30 mm|12 V DC|0.17 m³/min|890",
                "97 × 33 mm 12 V|97 × 94 × 33 mm|12 V DC|0.33 m³/min|1150",
                "97 × 33 mm 24 V|97 × 94 × 33 mm|24 V DC|0.38 m³/min|1250",
                "120 × 32 mm 24 V|120 × 120 × 32 mm|24 V DC|0.62 m³/min|1650"
            ],
            Title = "{Brand} BFB-series DC Centrifugal Blower {Size}, {Voltage}",
            Intro = "A compact DC centrifugal blower ({Size}) producing {Airflow} at {Voltage}. Higher static pressure than an axial fan makes it suitable for spot cooling, heatsinks and ducted airflow.",
            Highlights = ["{Size}, {Voltage}", "Airflow {Airflow}", "High static pressure", "Ball-bearing construction"],
            Applications = ["Heatsink cooling", "Spot cooling in equipment", "Ducted airflow"],
            Specs = ["General:Model=Model", "Mechanical:Size=Dimensions", "Electrical:Voltage=Rated voltage;Airflow=Airflow"],
            Fixed = ["Environment:Operating temperature=-10 to 70 °C"],
            Shape = Shape.Cylinder,
            WeightKg = 0.14m,
            BoxCm = (14m, 14m, 6m),
            Popularity = 0.5
        };

        // ───────────────────────── 6. Power Distribution & Protection ─────────────────────────
        yield return new Family
        {
            Leaf = "miniature-circuit-breakers",
            Brand = "Schneider Electric",
            Series = "Acti9 iC60N",
            NounEn = "MCB",
            NounBn = "এমসিবি",
            Header = "Model|Rating|Reference|Price",
            Rows =
            [
                "iC60N C6|6 A|A9F79106|620",
                "iC60N C10|10 A|A9F79110|620",
                "iC60N C16|16 A|A9F79116|620",
                "iC60N C20|20 A|A9F79120|650",
                "iC60N C25|25 A|A9F79125|650",
                "iC60N C32|32 A|A9F79132|690",
                "iC60N C40|40 A|A9F79140|760",
                "iC60N C63|63 A|A9F79163|980"
            ],
            Title = "{Brand} {Series} {Model} Miniature Circuit Breaker {Rating}, C Curve, 6 kA",
            Intro = "The Acti9 {Model} is a {Rating} C-curve miniature circuit breaker with 6 kA breaking capacity (IEC 60898-1) and 10 kA to IEC 60947-2. Supplied in one to four pole versions on 35 mm DIN rail (reference shown is the 1-pole version).",
            Highlights = ["{Rating} C-curve, 6 kA (IEC 60898-1)", "Choice of 1P, 2P, 3P and 4P", "Visible contact position indicator", "Reference (1P): {Reference}"],
            Applications = ["Lighting and socket circuits", "Motor control circuit protection", "Distribution boards"],
            Specs = ["General:Model=Model;Reference=Reference (1P)", "Electrical:Rating=Rated current"],
            Fixed = ["Electrical:Tripping curve=C", "Electrical:Breaking capacity=6 kA (IEC 60898-1)", "Electrical:Rated voltage=230/400 V AC", "Mechanical:Mounting=35 mm DIN rail"],
            Shape = Shape.DinModule,
            WeightKg = 0.12m,
            BoxCm = (9m, 5m, 8m),
            Popularity = 2.0,
            Axes = ["Poles|1P:1.00;2P:2.05;3P:3.10;4P:4.20"],
            FilterSpecs = ["Rated current"]
        };

        yield return new Family
        {
            Leaf = "miniature-circuit-breakers",
            Brand = "Hager",
            Series = "MBN",
            NounEn = "MCB",
            NounBn = "এমসিবি",
            Header = "Model|Rating|Price",
            Rows =
            [
                "MBN110|10 A|520",
                "MBN116|16 A|520",
                "MBN120|20 A|550",
                "MBN125|25 A|550",
                "MBN132|32 A|590",
                "MBN163|63 A|860"
            ],
            Title = "{Brand} {Model} MCB {Rating} C-curve, 6 kA",
            Intro = "The Hager {Model} is a {Rating} C-curve MCB with 6 kA breaking capacity. A robust, easy-to-wire design with finger-safe terminals and a visible position indicator for domestic, commercial and light industrial boards.",
            Highlights = ["{Rating}, C curve, 6 kA", "Available 1P–4P", "Finger-safe terminals", "35 mm DIN-rail mounting"],
            Applications = ["Final circuits", "Control panels", "Commercial distribution boards"],
            Specs = ["General:Model=Model", "Electrical:Rating=Rated current"],
            Fixed = ["Electrical:Tripping curve=C", "Electrical:Breaking capacity=6 kA", "Electrical:Rated voltage=230/400 V AC"],
            Shape = Shape.DinModule,
            WeightKg = 0.11m,
            BoxCm = (9m, 5m, 8m),
            Popularity = 1.2,
            Axes = ["Poles|1P:1.00;2P:2.05;3P:3.10;4P:4.20"],
            FilterSpecs = ["Rated current"]
        };

        yield return new Family
        {
            Leaf = "moulded-case-circuit-breakers",
            Brand = "Schneider Electric",
            Series = "EasyPact EZC",
            NounEn = "MCCB",
            NounBn = "এমসিসিবি",
            Header = "Model|Rating|Frame|Price",
            Rows =
            [
                "EZC100N3040|40 A|100 A frame|6850",
                "EZC100N3050|50 A|100 A frame|6850",
                "EZC100N3063|63 A|100 A frame|7250",
                "EZC100N3080|80 A|100 A frame|7650",
                "EZC100N3100|100 A|100 A frame|8250",
                "EZC250N3160|160 A|250 A frame|17500",
                "EZC250N3200|200 A|250 A frame|19500",
                "EZC250N3250|250 A|250 A frame|22500"
            ],
            Title = "{Brand} {Series} {Model} 3-pole MCCB {Rating} ({Frame})",
            Intro = "The EasyPact {Model} is a 3-pole moulded-case circuit breaker rated {Rating} in a {Frame}. Thermal-magnetic protection with fixed magnetic and adjustable thermal settings suit motor feeders and sub-distribution boards.",
            Highlights = ["3-pole, {Rating}, {Frame}", "Thermal-magnetic trip unit", "Breaking capacity 25 kA at 415 V", "Front-operated, accessory-ready"],
            Applications = ["Main and sub-distribution boards", "Motor feeders", "Machine incomers"],
            Specs = ["General:Model=Model", "Electrical:Rating=Rated current;Frame=Frame"],
            Fixed = ["Electrical:Poles=3", "Electrical:Rated voltage=415 V AC", "Electrical:Breaking capacity=25 kA at 415 V"],
            Shape = Shape.DinModule,
            WeightKg = 1.1m,
            BoxCm = (16m, 12m, 9m),
            Popularity = 1.0,
            FilterSpecs = ["Rated current"]
        };

        yield return new Family
        {
            Leaf = "moulded-case-circuit-breakers",
            Brand = "ABB",
            Series = "Tmax XT",
            NounEn = "MCCB",
            NounBn = "এমসিসিবি",
            Header = "Model|Rating|Frame|Breaking|Price",
            Rows =
            [
                "XT1N 160 TMD 63|63 A|160 A frame|36 kA|12500",
                "XT1N 160 TMD 100|100 A|160 A frame|36 kA|13500",
                "XT1N 160 TMD 125|125 A|160 A frame|36 kA|14500",
                "XT1N 160 TMD 160|160 A|160 A frame|36 kA|16500",
                "XT3N 250 TMD 200|200 A|250 A frame|36 kA|26500",
                "XT3N 250 TMD 250|250 A|250 A frame|36 kA|29500"
            ],
            Title = "{Brand} Tmax {Model} 3-pole MCCB {Rating}, {Breaking}",
            Intro = "The Tmax {Model} is a 3-pole thermal-magnetic MCCB, {Rating} in a {Frame} with {Breaking} breaking capacity at 415 V (Icu). A compact build and wide accessory range suit motor control centres and distribution panels.",
            Highlights = ["{Rating}, {Frame}", "Icu {Breaking} at 415 V", "Adjustable thermal, fixed magnetic", "Wide accessory range (shunt trip, aux contacts)"],
            Applications = ["Motor control centres", "Main distribution", "Generator incomers"],
            Specs = ["General:Model=Model", "Electrical:Rating=Rated current;Frame=Frame;Breaking=Icu at 415 V"],
            Fixed = ["Electrical:Poles=3", "Electrical:Rated voltage=690 V AC"],
            Shape = Shape.DinModule,
            WeightKg = 1.4m,
            BoxCm = (16m, 12m, 10m),
            Popularity = 0.8,
            FilterSpecs = ["Rated current"]
        };

        yield return new Family
        {
            Leaf = "power-contactors",
            Brand = "Schneider Electric",
            Series = "TeSys D",
            NounEn = "Contactor",
            NounBn = "কন্টাক্টর",
            Header = "Model|Current|Power|Price",
            Rows =
            [
                "LC1D09|9 A|4 kW @ 400 V|2450",
                "LC1D12|12 A|5.5 kW @ 400 V|2750",
                "LC1D18|18 A|7.5 kW @ 400 V|3450",
                "LC1D25|25 A|11 kW @ 400 V|4650",
                "LC1D32|32 A|15 kW @ 400 V|6150",
                "LC1D38|38 A|18.5 kW @ 400 V|7450"
            ],
            Title = "{Brand} {Series} {Model} 3-pole Contactor {Current}, {Power}",
            Intro = "The TeSys D {Model} is a 3-pole AC-3 contactor rated {Current} ({Power}) with one NO + one NC auxiliary contact. Coil voltage is selected at purchase; add LRD overload relays for a complete motor starter.",
            Highlights = ["AC-3 rating {Current} / {Power}", "1 NO + 1 NC auxiliary contacts", "Choice of coil voltage", "Finger-safe screw-clamp terminals"],
            Applications = ["Direct-on-line motor starters", "Heater and lighting contactors", "Pump and compressor control"],
            Specs = ["General:Model=Model", "Electrical:Current=AC-3 current;Power=Motor power"],
            Fixed = ["Electrical:Poles=3", "Electrical:Rated voltage=690 V AC", "Mechanical:Mounting=35 mm DIN rail or screw"],
            Shape = Shape.DinModule,
            WeightKg = 0.35m,
            BoxCm = (9m, 7m, 9m),
            Popularity = 1.8,
            Axes = ["Coil voltage|24 V DC:1.04;24 V AC:1.00;110 V AC:1.00;230 V AC:1.00"],
            FilterSpecs = ["AC-3 current", "Motor power"]
        };

        yield return new Family
        {
            Leaf = "power-contactors",
            Brand = "CHINT",
            Series = "NC1",
            NounEn = "Contactor",
            NounBn = "কন্টাক্টর",
            Header = "Model|Current|Power|Price",
            Rows =
            [
                "NC1-0910|9 A|4 kW @ 400 V|1450",
                "NC1-1210|12 A|5.5 kW @ 400 V|1650",
                "NC1-1810|18 A|7.5 kW @ 400 V|2150",
                "NC1-2510|25 A|11 kW @ 400 V|2850",
                "NC1-3210|32 A|15 kW @ 400 V|3850",
                "NC1-4011|40 A|18.5 kW @ 400 V|5150"
            ],
            Title = "{Brand} {Model} 3-pole AC Contactor {Current}, {Power}",
            Intro = "The CHINT {Model} is an economical 3-pole AC contactor rated {Current} ({Power}) with silver-alloy contacts. Widely used by panel builders for pump, fan and conveyor motor starters.",
            Highlights = ["{Current} AC-3 / {Power}", "Silver-alloy contacts", "Choice of coil voltage", "35 mm DIN-rail mounting"],
            Applications = ["Motor starters", "Heater contactors", "General panel switching"],
            Specs = ["General:Model=Model", "Electrical:Current=AC-3 current;Power=Motor power"],
            Fixed = ["Electrical:Poles=3", "Electrical:Rated voltage=690 V AC"],
            Shape = Shape.DinModule,
            WeightKg = 0.3m,
            BoxCm = (9m, 7m, 9m),
            Popularity = 1.5,
            Axes = ["Coil voltage|24 V AC:1.00;110 V AC:1.00;220 V AC:1.00;380 V AC:1.02"]
        };

        yield return new Family
        {
            Leaf = "power-contactors",
            Brand = "ABB",
            Series = "AF Series",
            NounEn = "Contactor",
            NounBn = "কন্টাক্টর",
            Header = "Model|Current|Power|Price",
            Rows =
            [
                "AF09-30-10-13|9 A|4 kW @ 400 V|3850",
                "AF12-30-10-13|12 A|5.5 kW @ 400 V|4350",
                "AF16-30-10-13|16 A|7.5 kW @ 400 V|5250",
                "AF26-30-10-13|26 A|11 kW @ 400 V|7250",
                "AF38-30-00-13|38 A|18.5 kW @ 400 V|11500"
            ],
            Title = "{Brand} {Model} Contactor {Current}, Coil 100–250 V AC/DC",
            Intro = "The ABB AF series {Model} is a {Current} ({Power}) 3-pole contactor with a wide-range electronic coil (100–250 V AC/DC), so one coil version covers the voltages found in most plants.",
            Highlights = ["{Current} AC-3 / {Power}", "100–250 V AC/DC wide-range coil", "Low coil consumption, built-in surge suppression", "Compatible with TF overload relays"],
            Applications = ["Motor starters", "Wide-voltage panels", "Retrofit"],
            Specs = ["General:Model=Model", "Electrical:Current=AC-3 current;Power=Motor power"],
            Fixed = ["Electrical:Poles=3", "Electrical:Coil=100–250 V AC/DC"],
            Shape = Shape.DinModule,
            WeightKg = 0.4m,
            BoxCm = (9m, 8m, 10m),
            Popularity = 0.9
        };

        yield return new Family
        {
            Leaf = "thermal-overload-relays",
            Brand = "Schneider Electric",
            Series = "TeSys LRD",
            NounEn = "Thermal Overload Relay",
            NounBn = "থার্মাল ওভারলোড রিলে",
            Header = "Model|Range|Suits|Price",
            Rows =
            [
                "LRD06|1–1.6 A|LC1D09–D38|1450",
                "LRD07|1.6–2.5 A|LC1D09–D38|1450",
                "LRD08|2.5–4 A|LC1D09–D38|1450",
                "LRD10|4–6 A|LC1D09–D38|1450",
                "LRD12|5.5–8 A|LC1D09–D38|1450",
                "LRD14|7–10 A|LC1D09–D38|1450",
                "LRD16|9–13 A|LC1D09–D38|1450",
                "LRD21|12–18 A|LC1D09–D38|1450"
            ],
            Title = "{Brand} {Model} Thermal Overload Relay {Range}",
            Intro = "The TeSys {Model} is a class-10A thermal overload relay with {Range} adjustment range for use with {Suits} contactors. Trip class 10A, phase-loss sensitivity, a manual/auto reset selector and a visible trip indicator.",
            Highlights = ["Adjustable {Range}", "Class 10A, phase-loss sensitive", "Manual / automatic reset", "Mounts directly under TeSys D contactors"],
            Applications = ["Motor overload protection", "Direct-on-line starters"],
            Specs = ["General:Model=Model", "Electrical:Range=Current setting range"],
            Fixed = ["Electrical:Trip class=10A", "Electrical:Auxiliary contacts=1 NO + 1 NC"],
            Shape = Shape.DinModule,
            WeightKg = 0.18m,
            BoxCm = (8m, 6m, 8m),
            Popularity = 1.2
        };

        yield return new Family
        {
            Leaf = "din-rail-power-supplies",
            Brand = "Mean Well",
            Series = "DIN-rail SMPS",
            NounEn = "DIN-rail Power Supply",
            NounBn = "ডিআইএন-রেল পাওয়ার সাপ্লাই",
            Header = "Model|Output|Power|Price",
            Rows =
            [
                "MDR-10-24|24 V DC / 0.42 A|10 W|1650",
                "MDR-20-24|24 V DC / 1 A|20 W|1950",
                "MDR-40-24|24 V DC / 1.7 A|40 W|2650",
                "MDR-60-24|24 V DC / 2.5 A|60 W|3250",
                "MDR-100-24|24 V DC / 4 A|100 W|4850",
                "HDR-60-24|24 V DC / 2.5 A|60 W|3950",
                "HDR-100-24|24 V DC / 4.2 A|100 W|5450",
                "NDR-120-24|24 V DC / 5 A|120 W|6850",
                "NDR-240-24|24 V DC / 10 A|240 W|11500",
                "NDR-480-24|24 V DC / 20 A|480 W|21500"
            ],
            Title = "{Brand} {Model} DIN-rail Power Supply {Output}, {Power}",
            Intro = "The Mean Well {Model} is a {Power} single-output DIN-rail power supply delivering {Output}, with universal AC input, short-circuit, overload and over-voltage protection. One of the most common 24 V supplies in Bangladeshi control panels.",
            Highlights = ["{Output} / {Power}", "Universal 85–264 V AC input", "Short-circuit, overload, over-voltage protection", "3-year manufacturer warranty on most models"],
            Applications = ["Control-panel 24 V supply", "PLC and sensor power", "LED and automation loads"],
            Specs = ["General:Model=Model", "Electrical:Output=Output;Power=Rated power"],
            Fixed = ["Electrical:Input voltage=85–264 V AC", "Environment:Operating temperature=-20 to 70 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.3m,
            BoxCm = (12m, 9m, 8m),
            Popularity = 2.0,
            WarrantyMonths = 36,
            FilterSpecs = ["Rated power"]
        };

        yield return new Family
        {
            Leaf = "relays-timers",
            Brand = "Omron",
            Series = "MY / G2R / H3CR",
            NounEn = "Relay / Timer",
            NounBn = "রিলে / টাইমার",
            Header = "Model|Type|Coil|Contacts|Price",
            Rows =
            [
                "MY2N-GS 24 VDC|Miniature power relay|24 V DC|2 C/O, 5 A|650",
                "MY4N-GS 24 VDC|Miniature power relay|24 V DC|4 C/O, 3 A|780",
                "G2R-1-SN 24 VDC|PCB / socket power relay|24 V DC|1 C/O, 16 A|520",
                "G2R-2-SN 24 VDC|PCB / socket power relay|24 V DC|2 C/O, 8 A|620",
                "H3CR-A8 24–48 VDC / 100–240 VAC|Multifunction timer (8-pin)|24–48 V DC / 100–240 V AC|1 C/O + 1 instantaneous, 5 A|4850",
                "H3Y-2 24 VDC (10 s)|Solid-state on-delay timer|24 V DC|2 C/O, 5 A|1950"
            ],
            Title = "{Brand} {Model} {Type}",
            Intro = "The Omron {Model} is a {Type} with {Coil} coil and {Contacts} contacts. Omron relays and timers are a standard part of PLC output interface and sequence-control circuits.",
            Highlights = ["{Type}", "Coil {Coil}, contacts {Contacts}", "Mechanical life 10 million operations", "Socket sold separately"],
            Applications = ["PLC output interfacing", "Sequence control", "Delay and timing circuits"],
            Specs = ["General:Model=Model;Type=Type", "Electrical:Coil=Coil voltage;Contacts=Contacts"],
            Fixed = ["Environment:Operating temperature=-25 to 55 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.05m,
            BoxCm = (6m, 5m, 6m),
            Popularity = 1.3
        };
    }
}
