namespace ShopVerse.Infrastructure.Seed;

/// <summary>Roots 7–9: pneumatics & fluid power, industrial networking & cables, control panels & enclosures.</summary>
internal static class FamiliesFluidNetworkPanel
{
    public static IEnumerable<Family> All()
    {
        // ───────────────────────── 7. Pneumatics & Fluid Power ─────────────────────────
        yield return new Family
        {
            Leaf = "iso-cylinders",
            Brand = "Festo",
            Series = "DSBC",
            NounEn = "ISO Pneumatic Cylinder",
            NounBn = "আইএসও নিউমেটিক সিলিন্ডার",
            Header = "Model|Bore|Force|Price",
            Rows =
            [
                "DSBC-32-PPVA-N3|32 mm|483 N at 6 bar|14500",
                "DSBC-40-PPVA-N3|40 mm|754 N at 6 bar|17500",
                "DSBC-50-PPVA-N3|50 mm|1 178 N at 6 bar|21500",
                "DSBC-63-PPVA-N3|63 mm|1 870 N at 6 bar|26500",
                "DSBC-80-PPVA-N3|80 mm|3 016 N at 6 bar|35500",
                "DSBC-100-PPVA-N3|100 mm|4 712 N at 6 bar|46500"
            ],
            Title = "{Brand} {Model} ISO 15552 Double-acting Cylinder, Ø{Bore}",
            Intro = "The Festo {Model} is a double-acting pneumatic cylinder to ISO 15552 with Ø{Bore} piston (theoretical force {Force}), adjustable pneumatic end-position cushioning (PPS) and a magnetic piston for proximity sensing. Select the stroke length that suits the machine.",
            Highlights = ["ISO 15552, Ø{Bore}, {Force}", "Adjustable pneumatic cushioning (PPV)", "Magnetic piston for SME/SMT-8 sensors", "Corrosion-resistant profile barrel, 10-bar rated"],
            Applications = ["Clamping and pressing", "Pushers and diverters", "Lifting and positioning"],
            Specs = ["General:Model=Model", "Mechanical:Bore=Piston diameter;Force=Theoretical force"],
            Fixed = ["Mechanical:Operating pressure=1–10 bar", "Mechanical:Standard=ISO 15552 (VDMA 24562)", "Environment:Temperature=-20 to 80 °C"],
            Shape = Shape.Cylinder,
            WeightKg = 0.9m,
            BoxCm = (40m, 10m, 10m),
            Popularity = 1.0,
            Axes = ["Stroke|50 mm:1.00;100 mm:1.12;160 mm:1.25;200 mm:1.34;250 mm:1.45"],
            FilterSpecs = ["Piston diameter"]
        };

        yield return new Family
        {
            Leaf = "iso-cylinders",
            Brand = "AirTAC",
            Series = "SI Series",
            NounEn = "ISO Pneumatic Cylinder",
            NounBn = "আইএসও নিউমেটিক সিলিন্ডার",
            Header = "Model|Bore|Force|Price",
            Rows =
            [
                "SI32-S|32 mm|483 N at 6 bar|4850",
                "SI40-S|40 mm|754 N at 6 bar|5850",
                "SI50-S|50 mm|1 178 N at 6 bar|7250",
                "SI63-S|63 mm|1 870 N at 6 bar|9250",
                "SI80-S|80 mm|3 016 N at 6 bar|12500"
            ],
            Title = "{Brand} {Model} ISO 6431 Standard Cylinder Ø{Bore}",
            Intro = "The AirTAC {Model} is an ISO 6431 standard double-acting cylinder with Ø{Bore} piston, adjustable cushioning and a magnet ring for sensor mounting. A cost-effective, widely stocked alternative on general-purpose machinery.",
            Highlights = ["ISO 6431, Ø{Bore}", "Adjustable cushioning both ends", "Magnetic piston", "Wide choice of mounting accessories"],
            Applications = ["General machinery", "Packaging lines", "Material handling"],
            Specs = ["General:Model=Model", "Mechanical:Bore=Piston diameter;Force=Theoretical force"],
            Fixed = ["Mechanical:Operating pressure=1–9 bar", "Mechanical:Standard=ISO 6431", "Environment:Temperature=-5 to 70 °C"],
            Shape = Shape.Cylinder,
            WeightKg = 0.8m,
            BoxCm = (40m, 10m, 10m),
            Popularity = 1.3,
            Axes = ["Stroke|50 mm:1.00;100 mm:1.10;150 mm:1.20;200 mm:1.30;250 mm:1.40"],
            FilterSpecs = ["Piston diameter"]
        };

        yield return new Family
        {
            Leaf = "compact-cylinders",
            Brand = "Festo",
            Series = "ADN",
            NounEn = "Compact Cylinder",
            NounBn = "কমপ্যাক্ট সিলিন্ডার",
            Header = "Model|Bore|Force|Price",
            Rows =
            [
                "ADN-20-A-P-A|20 mm|188 N at 6 bar|6850",
                "ADN-25-A-P-A|25 mm|295 N at 6 bar|7850",
                "ADN-32-A-P-A|32 mm|483 N at 6 bar|9450",
                "ADN-40-A-P-A|40 mm|754 N at 6 bar|11500",
                "ADN-50-A-P-A|50 mm|1 178 N at 6 bar|14500"
            ],
            Title = "{Brand} {Model} Compact Cylinder Ø{Bore} to ISO 21287",
            Intro = "The Festo {Model} is a short-stroke compact cylinder (ISO 21287) with Ø{Bore} piston and elastic cushioning rings, providing {Force} in very little installation space.",
            Highlights = ["ISO 21287 compact design, Ø{Bore}", "Elastic cushioning, magnetic piston", "Internal thread or through-hole fixing", "Stroke options 10–100 mm"],
            Applications = ["Clamping and stamping", "Short-stroke pushers", "Tight-space automation"],
            Specs = ["General:Model=Model", "Mechanical:Bore=Piston diameter;Force=Theoretical force"],
            Fixed = ["Mechanical:Operating pressure=1–10 bar", "Mechanical:Standard=ISO 21287"],
            Shape = Shape.Cylinder,
            WeightKg = 0.35m,
            BoxCm = (20m, 10m, 10m),
            Popularity = 0.9,
            Axes = ["Stroke|10 mm:1.00;20 mm:1.05;25 mm:1.08;50 mm:1.20;80 mm:1.35"],
            FilterSpecs = ["Piston diameter"]
        };

        yield return new Family
        {
            Leaf = "compact-cylinders",
            Brand = "AirTAC",
            Series = "ACQ",
            NounEn = "Compact Cylinder",
            NounBn = "কমপ্যাক্ট সিলিন্ডার",
            Header = "Model|Bore|Price",
            Rows =
            [
                "ACQ25|25 mm|2250",
                "ACQ32|32 mm|2650",
                "ACQ40|40 mm|3250",
                "ACQ50|50 mm|4250",
                "ACQ63|63 mm|5450"
            ],
            Title = "{Brand} {Model} Compact Cylinder Ø{Bore}",
            Intro = "The AirTAC {Model} is an economical compact double-acting cylinder with Ø{Bore} piston, aluminium barrel and magnetic piston, available in short strokes for tight installations.",
            Highlights = ["Ø{Bore} double-acting", "Magnetic piston", "Aluminium alloy barrel", "Stroke 5–100 mm"],
            Applications = ["Pushers and clamps", "Small automation", "Packaging machines"],
            Specs = ["General:Model=Model", "Mechanical:Bore=Piston diameter"],
            Fixed = ["Mechanical:Operating pressure=0.1–1.0 MPa", "Environment:Temperature=-5 to 70 °C"],
            Shape = Shape.Cylinder,
            WeightKg = 0.25m,
            BoxCm = (18m, 9m, 9m),
            Popularity = 1.2,
            Axes = ["Stroke|10 mm:1.00;25 mm:1.10;50 mm:1.25;75 mm:1.40"],
            FilterSpecs = ["Piston diameter"]
        };

        yield return new Family
        {
            Leaf = "solenoid-valves",
            Brand = "AirTAC",
            Series = "4V200 Series",
            NounEn = "5/2 Solenoid Valve",
            NounBn = "৫/২ সলিনয়েড ভালভ",
            Header = "Model|Port|Function|Price",
            Rows =
            [
                "4V110-06|1/8″|5/2 single solenoid, spring return|1450",
                "4V210-08|1/4″|5/2 single solenoid, spring return|1550",
                "4V220-08|1/4″|5/2 double solenoid|1950",
                "4V310-10|3/8″|5/2 single solenoid, spring return|2250",
                "4V320-10|3/8″|5/2 double solenoid|2750"
            ],
            Title = "{Brand} {Model} {Function} Pneumatic Valve, {Port}",
            Intro = "The AirTAC {Model} is a {Function} valve with {Port} ports for cylinder control. Coil voltage is chosen at purchase; a manual override and LED indicator come with the coil.",
            Highlights = ["{Function}, {Port} ports", "Manual override", "Pilot-operated spool valve", "Choice of coil voltage"],
            Applications = ["Cylinder control", "Clamping and sorting", "Packaging machines"],
            Specs = ["General:Model=Model;Function=Valve function", "Mechanical:Port=Port size"],
            Fixed = ["Mechanical:Operating pressure=0.15–0.8 MPa", "Environment:Temperature=-5 to 60 °C"],
            Shape = Shape.Valve,
            WeightKg = 0.22m,
            BoxCm = (12m, 8m, 7m),
            Popularity = 1.3,
            Axes = ["Coil voltage|24 V DC:1.00;110 V AC:1.00;220 V AC:1.00"]
        };

        yield return new Family
        {
            Leaf = "solenoid-valves",
            Brand = "SMC",
            Series = "SY Series",
            NounEn = "5-port Solenoid Valve",
            NounBn = "৫-পোর্ট সলিনয়েড ভালভ",
            Header = "Model|Function|Port|Price",
            Rows =
            [
                "SY3120-5LZD-M5|5/2 single solenoid|M5|3250",
                "SY5120-5LZD-01|5/2 single solenoid|1/8″|3850",
                "SY5220-5LZD-01|5/2 double solenoid|1/8″|4650",
                "SY7120-5LZD-02|5/2 single solenoid|1/4″|4850"
            ],
            Title = "{Brand} {Model} {Function} Valve, {Port} Ports",
            Intro = "The SMC {Model} from the SY series is a pilot-operated {Function} valve with {Port} ports, light-weight and energy-saving, with plug-lead (L) connector and LED/surge-suppressor (Z) for 24 V DC.",
            Highlights = ["{Function}, {Port}", "Plug-in lead with LED and surge suppressor", "Manual override (locking)", "Long service life"],
            Applications = ["Cylinder control on assembly machines", "Valve manifolds", "Pick-and-place"],
            Specs = ["General:Model=Model;Function=Function", "Mechanical:Port=Port size"],
            Fixed = ["Electrical:Coil=24 V DC", "Mechanical:Operating pressure=0.1–0.7 MPa"],
            Shape = Shape.Valve,
            WeightKg = 0.15m,
            BoxCm = (11m, 7m, 6m),
            Popularity = 0.9
        };

        yield return new Family
        {
            Leaf = "frl-units",
            Brand = "AirTAC",
            Series = "GFC Series",
            NounEn = "FRL Unit",
            NounBn = "এফআরএল ইউনিট",
            Header = "Model|Port|Flow|Price",
            Rows =
            [
                "GFC200-08|1/4″|~1 300 NL/min|2850",
                "GFC300-10|3/8″|~2 600 NL/min|3650",
                "GFC400-15|1/2″|~4 500 NL/min|5250",
                "GFR200-08|1/4″|filter + regulator|1850",
                "GFR300-10|3/8″|filter + regulator|2450"
            ],
            Title = "{Brand} {Model} Air Preparation Unit, {Port} ({Flow})",
            Intro = "The AirTAC {Model} conditions compressed air before it reaches valves and cylinders: filter, regulator and (on GFC models) lubricator in one modular assembly with {Port} ports and a pressure gauge.",
            Highlights = ["{Port} ports, flow {Flow}", "Includes pressure gauge", "Auto or manual drain", "Modular design"],
            Applications = ["Machine air supply conditioning", "Protecting valves and cylinders"],
            Specs = ["General:Model=Model", "Mechanical:Port=Port size;Flow=Flow rate"],
            Fixed = ["Mechanical:Max pressure=1.0 MPa", "Mechanical:Filtration=40 µm"],
            Shape = Shape.Gauge,
            WeightKg = 0.6m,
            BoxCm = (20m, 10m, 10m),
            Popularity = 1.0
        };

        yield return new Family
        {
            Leaf = "frl-units",
            Brand = "SMC",
            Series = "AC Series",
            NounEn = "FRL Modular Unit",
            NounBn = "এফআরএল মডুলার ইউনিট",
            Header = "Model|Port|Price",
            Rows =
            [
                "AC20-02-A|1/4″|6850",
                "AC30-03-A|3/8″|9450",
                "AC40-04-A|1/2″|13500"
            ],
            Title = "{Brand} {Model} Modular F.R.L. Unit, {Port}",
            Intro = "The SMC {Model} combines filter, regulator and lubricator in a compact modular air-preparation set with {Port} ports, one of the most widely specified FRL families worldwide.",
            Highlights = ["{Port} ports", "Filter + regulator + lubricator", "Integrated pressure gauge", "Modular bracket mounting"],
            Applications = ["Machine air supply", "Valve manifolds"],
            Specs = ["General:Model=Model", "Mechanical:Port=Port size"],
            Fixed = ["Mechanical:Max pressure=1.0 MPa"],
            Shape = Shape.Gauge,
            WeightKg = 0.8m,
            BoxCm = (22m, 11m, 11m),
            Popularity = 0.7
        };

        yield return new Family
        {
            Leaf = "push-in-fittings",
            Brand = "AirTAC",
            Series = "PC / PL / PU",
            NounEn = "Push-in Fitting",
            NounBn = "পুশ-ইন ফিটিং",
            Header = "Model|Type|Tube|Thread|Price",
            Rows =
            [
                "PC6-01|Straight male connector|Ø6 mm|R1/8″|95",
                "PC6-02|Straight male connector|Ø6 mm|R1/4″|105",
                "PC8-02|Straight male connector|Ø8 mm|R1/4″|115",
                "PC8-03|Straight male connector|Ø8 mm|R3/8″|135",
                "PL6-02|Elbow male connector|Ø6 mm|R1/4″|125",
                "PL8-02|Elbow male connector|Ø8 mm|R1/4″|140",
                "PU6|Union straight|Ø6 mm|—|110"
            ],
            Title = "{Brand} {Model} {Type} for {Tube} Tube ({Thread})",
            Intro = "The AirTAC {Model} is a nickel-plated brass {Type} with push-to-connect release collar for {Tube} tubing and {Thread} thread. Quick, tool-free assembly for pneumatic lines.",
            Highlights = ["{Type}", "{Tube} tube, {Thread}", "Nickel-plated brass body", "Re-usable push-to-connect"],
            Applications = ["Pneumatic tubing connections", "Valve and cylinder ports"],
            Specs = ["General:Model=Model;Type=Type", "Mechanical:Tube=Tube size;Thread=Thread"],
            Fixed = ["Mechanical:Max pressure=1.0 MPa", "Mechanical:Material=Nickel-plated brass"],
            Shape = Shape.Fitting,
            WeightKg = 0.02m,
            BoxCm = (5m, 4m, 3m),
            Popularity = 1.4,
            Axes = ["Pack|Single piece:1.00;Pack of 10:9.00;Pack of 50:42.00"]
        };

        yield return new Family
        {
            Leaf = "push-in-fittings",
            Brand = "SMC",
            Series = "KQ2",
            NounEn = "One-touch Fitting",
            NounBn = "ওয়ান-টাচ ফিটিং",
            Header = "Model|Type|Tube|Thread|Price",
            Rows =
            [
                "KQ2H06-01S|Male connector|Ø6 mm|R1/8″|285",
                "KQ2H06-02S|Male connector|Ø6 mm|R1/4″|295",
                "KQ2H08-02S|Male connector|Ø8 mm|R1/4″|310",
                "KQ2L06-02S|Male elbow|Ø6 mm|R1/4″|325"
            ],
            Title = "{Brand} {Model} One-touch {Type} for {Tube} Tube, {Thread}",
            Intro = "The SMC {Model} is a KQ2-series one-touch fitting ({Type}) for {Tube} tube with {Thread} thread. Sealing is by NBR gasket pre-applied to the thread.",
            Highlights = ["{Type}", "{Tube} tube, {Thread}", "Pre-applied thread sealant", "Compact body"],
            Applications = ["Pneumatic valve and cylinder connections"],
            Specs = ["General:Model=Model;Type=Type", "Mechanical:Tube=Tube size;Thread=Thread"],
            Fixed = ["Mechanical:Max pressure=1.0 MPa"],
            Shape = Shape.Fitting,
            WeightKg = 0.02m,
            BoxCm = (5m, 4m, 3m),
            Popularity = 0.8,
            Axes = ["Pack|Single piece:1.00;Pack of 10:9.20"]
        };

        yield return new Family
        {
            Leaf = "pu-nylon-tubing",
            Brand = "Festo",
            Series = "PUN-H",
            NounEn = "PU Tubing",
            NounBn = "পিইউ টিউবিং",
            Header = "Model|Size|Colour|Price",
            Rows =
            [
                "PUN-H-6×1-BL|Ø6 × 1 mm|Blue|3450",
                "PUN-H-8×1.25-BL|Ø8 × 1.25 mm|Blue|4650",
                "PUN-H-10×1.5-BL|Ø10 × 1.5 mm|Blue|6450",
                "PUN-H-12×2-BL|Ø12 × 2 mm|Blue|8950"
            ],
            Title = "{Brand} {Model} Polyurethane Tubing {Size}, 50 m Roll",
            Intro = "Festo {Model} is a hydrolysis-resistant polyurethane pneumatic tube, {Size}, supplied in a 50 m roll. Flexible and abrasion-resistant; works with all standard push-in fittings.",
            Highlights = ["{Size}, polyurethane", "Hydrolysis and abrasion resistant", "50 m roll", "Operating pressure up to 10 bar"],
            Applications = ["Pneumatic connections", "Cylinder and valve plumbing"],
            Specs = ["General:Model=Model", "Mechanical:Size=Tube size;Colour=Colour"],
            Fixed = ["Mechanical:Material=Polyurethane (PUR)", "Environment:Temperature=-35 to 60 °C"],
            Shape = Shape.Cable,
            WeightKg = 1.1m,
            BoxCm = (30m, 30m, 10m),
            Popularity = 0.9
        };

        yield return new Family
        {
            Leaf = "pu-nylon-tubing",
            Brand = "SMC",
            Series = "TU",
            NounEn = "Polyurethane Tubing",
            NounBn = "পলিইউরেথেন টিউবিং",
            Header = "Model|Size|Price",
            Rows =
            [
                "TU0604B-20|Ø6 × 4 mm|2450",
                "TU0805B-20|Ø8 × 5 mm|3450",
                "TU1065B-20|Ø10 × 6.5 mm|4650",
                "TU1208B-20|Ø12 × 8 mm|5850"
            ],
            Title = "{Brand} {Model} Polyurethane Tubing {Size}, 20 m Roll (Blue)",
            Intro = "SMC {Model} polyurethane tubing, {Size}, 20 m roll in blue. Soft and flexible with excellent kink resistance, for one-touch fittings.",
            Highlights = ["{Size}, 20 m roll", "Kink resistant", "Suitable for one-touch fittings", "Max 0.8 MPa"],
            Applications = ["Pneumatic piping"],
            Specs = ["General:Model=Model", "Mechanical:Size=Tube size"],
            Fixed = ["Mechanical:Material=Polyurethane", "Environment:Temperature=-20 to 60 °C"],
            Shape = Shape.Cable,
            WeightKg = 0.6m,
            BoxCm = (26m, 26m, 8m),
            Popularity = 0.7
        };

        // ───────────────────────── 8. Industrial Networking & Cables ─────────────────────────
        yield return new Family
        {
            Leaf = "managed-ethernet-switches",
            Brand = "Moxa",
            Series = "EDS Managed",
            NounEn = "Managed Ethernet Switch",
            NounBn = "ম্যানেজড ইথারনেট সুইচ",
            Header = "Model|Ports|Uplinks|Price",
            Rows =
            [
                "EDS-508A|8 × 10/100M|—|58500",
                "EDS-516A|16 × 10/100M|—|88500",
                "EDS-518A|16 × 10/100M|2 × Gigabit combo|118500",
                "EDS-G508E|8 × Gigabit|—|98500",
                "EDS-G512E|12 × Gigabit|—|138500"
            ],
            Title = "{Brand} {Model} Managed Industrial Ethernet Switch, {Ports}",
            Intro = "The Moxa {Model} is a managed industrial Ethernet switch with {Ports} (uplinks: {Uplinks}). Turbo Ring redundancy (recovery under 20 ms), VLAN, IGMP snooping and SNMP management make it suitable for ETP SCADA, machine networks and substations.",
            Highlights = ["{Ports}, uplinks {Uplinks}", "Turbo Ring / Turbo Chain redundancy", "VLAN, QoS, IGMP, SNMP v1/v2c/v3", "-10 to 60 °C, DIN-rail"],
            Applications = ["SCADA and ETP networks", "Machine-to-line networks", "Substation automation"],
            Specs = ["General:Model=Model", "Communication:Ports=Ports;Uplinks=Uplinks"],
            Fixed = ["Electrical:Supply=12–48 V DC redundant", "Environment:Operating temperature=-10 to 60 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.9m,
            BoxCm = (24m, 14m, 10m),
            Popularity = 0.7,
            WarrantyMonths = 60
        };

        yield return new Family
        {
            Leaf = "managed-ethernet-switches",
            Brand = "Siemens",
            Series = "SCALANCE XC",
            NounEn = "Managed Ethernet Switch",
            NounBn = "ম্যানেজড ইথারনেট সুইচ",
            Header = "Model|MPN|Ports|Price",
            Rows =
            [
                "SCALANCE XC208|6GK5208-0BA00-2AC2|8 × 10/100M RJ45|68500",
                "SCALANCE XC216|6GK5216-0BA00-2AC2|16 × 10/100M RJ45|108500"
            ],
            Title = "{Brand} {Model} Managed Switch, {Ports} ({MPN})",
            Intro = "The {Model} is a managed layer-2 switch for PROFINET networks: {Ports}, integrated diagnostics in TIA Portal and web-based management.",
            Highlights = ["{Ports}", "PROFINET diagnostics in TIA Portal", "Ring redundancy (MRP)", "Redundant 24 V DC power"],
            Applications = ["PROFINET machine networks", "Plant backbones"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model", "Communication:Ports=Ports"],
            Fixed = ["Electrical:Supply=24 V DC redundant", "Environment:Operating temperature=-40 to 70 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.6m,
            BoxCm = (22m, 12m, 9m),
            Popularity = 0.5
        };

        yield return new Family
        {
            Leaf = "unmanaged-ethernet-switches",
            Brand = "Moxa",
            Series = "EDS Unmanaged",
            NounEn = "Unmanaged Ethernet Switch",
            NounBn = "আনম্যানেজড ইথারনেট সুইচ",
            Header = "Model|Ports|Speed|Price",
            Rows =
            [
                "EDS-205A|5|10/100M|7250",
                "EDS-208A|8|10/100M|9850",
                "EDS-305|5|10/100M|10250",
                "EDS-G205|5|Gigabit|18500",
                "EDS-G308|8|Gigabit|28500"
            ],
            Title = "{Brand} {Model} {Ports}-port {Speed} Unmanaged Industrial Switch",
            Intro = "The Moxa {Model} is a plug-and-play {Ports}-port industrial Ethernet switch ({Speed}) with a metal DIN-rail housing, wide-temperature operation and redundant power inputs on most models. No configuration needed.",
            Highlights = ["{Ports} ports, {Speed}", "IP30 metal housing", "Redundant 12–48 V DC inputs", "Broadcast storm protection"],
            Applications = ["Machine-level networks", "PLC and HMI connectivity"],
            Specs = ["General:Model=Model", "Communication:Ports=Ports;Speed=Speed"],
            Fixed = ["Environment:Operating temperature=-10 to 60 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.17m,
            BoxCm = (14m, 9m, 6m),
            Popularity = 1.2,
            WarrantyMonths = 60
        };

        yield return new Family
        {
            Leaf = "unmanaged-ethernet-switches",
            Brand = "Phoenix Contact",
            Series = "FL SWITCH",
            NounEn = "Unmanaged Ethernet Switch",
            NounBn = "আনম্যানেজড ইথারনেট সুইচ",
            Header = "Model|Ports|Price",
            Rows =
            [
                "FL SWITCH 1005N|5|8450",
                "FL SWITCH 1008N|8|11500"
            ],
            Title = "{Brand} {Model} {Ports}-port Unmanaged Ethernet Switch",
            Intro = "The Phoenix Contact {Model} is a compact {Ports}-port unmanaged switch with auto-negotiation and auto-crossover, mounted on 35 mm DIN rail in a rugged plastic housing.",
            Highlights = ["{Ports} × 10/100 Mbps RJ45", "Auto-negotiation, auto-crossing", "24 V DC supply", "Compact DIN-rail housing"],
            Applications = ["Machine and cabinet networks"],
            Specs = ["General:Model=Model", "Communication:Ports=Ports"],
            Fixed = ["Electrical:Supply=18–32 V DC", "Environment:Operating temperature=-40 to 75 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.14m,
            BoxCm = (12m, 8m, 5m),
            Popularity = 0.6
        };

        yield return new Family
        {
            Leaf = "serial-device-servers",
            Brand = "Moxa",
            Series = "NPort",
            NounEn = "Serial Device Server",
            NounBn = "সিরিয়াল ডিভাইস সার্ভার",
            Header = "Model|Serial ports|Interface|Price",
            Rows =
            [
                "NPort 5110|1|RS-232|16500",
                "NPort 5130|1|RS-422/485|17500",
                "NPort 5150|1|RS-232/422/485|18500",
                "NPort 5210|2|RS-232|24500",
                "NPort 5230|2|RS-422/485|26500",
                "NPort 5232|2|RS-422/485|27500",
                "NPort 5410|4|RS-232|42500",
                "NPort 5610-8|8|RS-232|62500"
            ],
            Title = "{Brand} {Model} {Serial ports}-port Serial Device Server ({Interface})",
            Intro = "The Moxa {Model} connects {Serial ports} serial device(s) ({Interface}) to an Ethernet network, with real-COM, TCP server/client and UDP modes. Typical for putting legacy meters, scales and PLC ports on SCADA.",
            Highlights = ["{Serial ports} × {Interface}", "Real COM / TCP / UDP modes", "Browser and utility configuration", "15 kV ESD protection"],
            Applications = ["Legacy serial devices to Ethernet", "Energy meters and scales", "SCADA integration"],
            Specs = ["General:Model=Model", "Communication:Serial ports=Serial ports;Interface=Serial interface"],
            Fixed = ["Electrical:Supply=12–48 V DC", "Environment:Operating temperature=0 to 55 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.3m,
            BoxCm = (14m, 11m, 7m),
            Popularity = 0.8,
            WarrantyMonths = 60
        };

        yield return new Family
        {
            Leaf = "industrial-routers",
            Brand = "Moxa",
            Series = "EDR / OnCell",
            NounEn = "Industrial Router",
            NounBn = "ইন্ডাস্ট্রিয়াল রাউটার",
            Header = "Model|Function|Ports|Price",
            Rows =
            [
                "EDR-810|Secure router with firewall / VPN|8 × 10/100M + 2 × Gigabit|188500",
                "EDR-G902|Secure router with firewall / VPN|Gigabit WAN + 2-port|198500",
                "EDR-G903|Secure router with firewall / VPN|Gigabit 3-port|215000",
                "OnCell G3150A-LTE|Cellular LTE gateway|2 × Ethernet + 1 × serial|98500"
            ],
            Title = "{Brand} {Model} Industrial {Function}",
            Intro = "The Moxa {Model} is an industrial {Function} with {Ports}. It secures remote access to ETP and machine networks via VPN and a stateful firewall, or provides 4G backhaul where wired connections are unavailable.",
            Highlights = ["{Function}", "Ports: {Ports}", "VPN, firewall and NAT", "-10 to 60 °C"],
            Applications = ["Remote access to ETP SCADA", "Secure cell/zone separation", "4G backhaul"],
            Specs = ["General:Model=Model;Function=Function", "Communication:Ports=Ports"],
            Fixed = ["Electrical:Supply=12–48 V DC"],
            Shape = Shape.DinModule,
            WeightKg = 0.9m,
            BoxCm = (22m, 16m, 10m),
            Popularity = 0.4,
            WarrantyMonths = 36
        };

        yield return new Family
        {
            Leaf = "industrial-routers",
            Brand = "Siemens",
            Series = "SCALANCE",
            NounEn = "Industrial Router",
            NounBn = "ইন্ডাস্ট্রিয়াল রাউটার",
            Header = "Model|MPN|Function|Price",
            Rows =
            [
                "SCALANCE S615|6GK5615-0AA00-2AA2|Security router, 5-port|152500",
                "SCALANCE M874-3|6GK5874-3AA00-2AA2|3G/4G cellular router|125500"
            ],
            Title = "{Brand} {Model} {Function} ({MPN})",
            Intro = "The {Model} is a {Function} for protecting and connecting automation cells, with firewall, VPN and diagnostics integrated into TIA Portal.",
            Highlights = ["{Function}", "Firewall and VPN", "TIA Portal integration"],
            Applications = ["Remote maintenance", "Network segmentation"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model;Function=Function"],
            Fixed = ["Electrical:Supply=24 V DC"],
            Shape = Shape.DinModule,
            WeightKg = 0.5m,
            BoxCm = (20m, 14m, 9m),
            Popularity = 0.3
        };

        yield return new Family
        {
            Leaf = "control-instrument-cables",
            Brand = "Havells",
            Series = "FR-PVC Control Cable",
            NounEn = "Flexible Control Cable",
            NounBn = "ফ্লেক্সিবল কন্ট্রোল ক্যাবল",
            Header = "Model|Cores|Size|Price",
            Rows =
            [
                "3 core × 0.75 mm²|3|0.75 mm²|4850",
                "4 core × 0.75 mm²|4|0.75 mm²|6050",
                "5 core × 1.0 mm²|5|1.0 mm²|9250",
                "7 core × 1.0 mm²|7|1.0 mm²|12500",
                "12 core × 0.75 mm²|12|0.75 mm²|18500",
                "3 core × 1.5 mm²|3|1.5 mm²|8350",
                "4 core × 2.5 mm²|4|2.5 mm²|18500"
            ],
            Title = "{Brand} {Model} Multicore Flexible Control Cable, 90 m Coil",
            Intro = "A {Cores}-core flexible copper control cable with {Size} conductors, 300/500 V PVC insulation and sheath, supplied on a 90 m coil. Fine-strand class-5 conductors tolerate vibration and repeated bending in panels and machine wiring.",
            Highlights = ["{Cores} cores × {Size} flexible copper", "300/500 V PVC insulation and sheath", "Class 5 fine-strand conductors", "90 m coil"],
            Applications = ["Control panel wiring", "Machine control circuits", "Sensor and signal wiring"],
            Specs = ["General:Model=Specification", "Electrical:Cores=Number of cores;Size=Conductor size"],
            Fixed = ["Electrical:Rated voltage=300/500 V", "Mechanical:Length=90 m coil", "Environment:Temperature=-5 to 70 °C"],
            Shape = Shape.Cable,
            WeightKg = 6m,
            BoxCm = (32m, 32m, 12m),
            Popularity = 1.3
        };

        yield return new Family
        {
            Leaf = "control-instrument-cables",
            Brand = "Siemens",
            Series = "SIMATIC NET",
            NounEn = "Industrial Network Cable",
            NounBn = "ইন্ডাস্ট্রিয়াল নেটওয়ার্ক ক্যাবল",
            Header = "Model|MPN|Type|Price",
            Rows =
            [
                "PROFIBUS FC Standard Cable GP|6XV1830-0EH10|PROFIBUS, per metre|620",
                "Industrial Ethernet FC TP Standard Cable GP 2×2|6XV1840-2AH10|Industrial Ethernet, per metre|480"
            ],
            Title = "{Brand} {Model} ({MPN}) — {Type}",
            Intro = "{Model}: a bus cable for fixed installation in {Type}. Sold per metre and cut to length; use FastConnect strippers for quick termination.",
            Highlights = ["{Type}", "Violet PVC sheath for PROFIBUS / green for Ethernet", "FastConnect compatible"],
            Applications = ["PROFIBUS DP networks", "PROFINET / Industrial Ethernet runs"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model;Type=Type"],
            Fixed = ["Mechanical:Installation=Fixed installation"],
            Shape = Shape.Cable,
            WeightKg = 0.07m,
            BoxCm = (10m, 10m, 4m),
            Popularity = 0.5
        };

        yield return new Family
        {
            Leaf = "m12-connectors-cordsets",
            Brand = "Phoenix Contact",
            Series = "SAC M12",
            NounEn = "M12 Cordset",
            NounBn = "এম১২ কর্ডসেট",
            Header = "Model|Pins|Style|Price",
            Rows =
            [
                "SAC-4P-M12MS/ 5,0-PUR|4|Straight male, open end|1650",
                "SAC-4P- 5,0-PUR/M12FS|4|Straight female, open end|1650",
                "SAC-4P- 5,0-PUR/M12FR|4|Right-angle female, open end|1850",
                "SAC-3P-M8MS/ 5,0-PUR|3|M8 straight male, open end|1450"
            ],
            Title = "{Brand} {Model} Sensor Cordset, {Pins}-pin, {Style}",
            Intro = "A pre-moulded {Pins}-pin sensor/actuator cordset ({Style}) with PUR cable and IP67-sealed coupling nut, for connecting sensors and valves without terminal work. Choose the cable length from the options.",
            Highlights = ["{Pins}-pin, {Style}", "PUR sheath (oil-resistant, halogen-free)", "IP67", "Knurled stainless/nickel coupling nut"],
            Applications = ["Sensor and valve connections", "Replacing damaged sensor cables"],
            Specs = ["General:Model=Model", "Mechanical:Pins=Pins;Style=Style"],
            Fixed = ["Environment:Degree of protection=IP67", "Electrical:Rated voltage=60 V AC / 75 V DC"],
            Shape = Shape.Cable,
            WeightKg = 0.12m,
            BoxCm = (16m, 10m, 4m),
            Popularity = 0.9,
            Axes = ["Cable length|2 m:0.80;5 m:1.00;10 m:1.65"]
        };

        yield return new Family
        {
            Leaf = "m12-connectors-cordsets",
            Brand = "IFM Electronic",
            Series = "EVC",
            NounEn = "M12 Cordset",
            NounBn = "এম১২ কর্ডসেট",
            Header = "Model|Pins|Style|Price",
            Rows =
            [
                "EVC001|4|Straight female, open end, 2 m|1450",
                "EVC002|4|Straight female, open end, 5 m|1850",
                "EVC004|4|Right-angle female, open end, 2 m|1650"
            ],
            Title = "{Brand} {Model} M12 Connection Cable, {Pins}-pin, {Style}",
            Intro = "The ifm {Model} is a M12 {Pins}-pin connection cable ({Style}) with PUR jacket. A sensor cable replacement that fits ifm and third-party M12 sensors.",
            Highlights = ["{Style}", "PUR jacket", "IP65/IP67/IP68"],
            Applications = ["Sensor replacement cables"],
            Specs = ["General:Model=Model", "Mechanical:Pins=Pins;Style=Style"],
            Fixed = ["Environment:Degree of protection=IP65 / IP67 / IP68"],
            Shape = Shape.Cable,
            WeightKg = 0.1m,
            BoxCm = (16m, 10m, 4m),
            Popularity = 0.7
        };

        // ───────────────────────── 9. Control Panels & Enclosures ─────────────────────────
        yield return new Family
        {
            Leaf = "wall-mount-steel-enclosures",
            Brand = "Rittal",
            Series = "AE Compact",
            NounEn = "Steel Enclosure",
            NounBn = "স্টিল এনক্লোজার",
            Header = "Model|Size|Price",
            Rows =
            [
                "AE 1007.500|300 × 200 × 155 mm (H × W × D)|11500",
                "AE 1050.500|380 × 300 × 210 mm (H × W × D)|18500",
                "AE 1060.500|600 × 380 × 210 mm (H × W × D)|29500",
                "AE 1380.500|800 × 600 × 300 mm (H × W × D)|62500",
                "AE 1480.500|1000 × 800 × 300 mm (H × W × D)|88500"
            ],
            Title = "{Brand} {Model} Sheet-steel Compact Enclosure {Size}",
            Intro = "The Rittal {Model} is a single-door sheet-steel compact enclosure, {Size}, with RAL 7035 powder coating, foamed-in seal and a mounting plate included. IP66-rated for dusty factory floors and wash-down areas.",
            Highlights = ["{Size}", "IP66, RAL 7035 powder-coated sheet steel", "Mounting plate included", "Door hinges reversible"],
            Applications = ["PLC and drive panels", "Terminal and instrument boxes", "Machine control cabinets"],
            Specs = ["General:Model=Model", "Mechanical:Size=Dimensions"],
            Fixed = ["Environment:Degree of protection=IP66", "Mechanical:Material=Sheet steel, RAL 7035", "Mechanical:Door=Single door with swing-handle lock"],
            Shape = Shape.Enclosure,
            WeightKg = 8m,
            BoxCm = (62m, 42m, 24m),
            Popularity = 1.0,
            WarrantyMonths = 24
        };

        yield return new Family
        {
            Leaf = "wall-mount-steel-enclosures",
            Brand = "Schneider Electric",
            Series = "Spacial S3D",
            NounEn = "Steel Enclosure",
            NounBn = "স্টিল এনক্লোজার",
            Header = "Model|Size|Price",
            Rows =
            [
                "NSYS3D4320P|400 × 300 × 200 mm|16500",
                "NSYS3D5420P|500 × 400 × 200 mm|22500",
                "NSYS3D6420P|600 × 400 × 200 mm|26500",
                "NSYS3D6630P|600 × 600 × 300 mm|38500",
                "NSYS3D8630P|800 × 600 × 300 mm|52500"
            ],
            Title = "{Brand} {Model} Spacial S3D Wall-mounted Enclosure with Plain Door, {Size}",
            Intro = "The Spacial S3D {Model} is a wall-mounted steel enclosure with plain door and mounting plate, {Size} (H × W × D). IP66 with polyurethane gasket; rack-mount and ventilation accessories fit without drilling.",
            Highlights = ["{Size} (H × W × D)", "IP66, polyurethane foamed gasket", "Includes mounting plate", "Accessory-ready"],
            Applications = ["Control panels", "Motor starter cabinets", "Instrument housings"],
            Specs = ["General:Model=Model", "Mechanical:Size=Dimensions (H × W × D)"],
            Fixed = ["Environment:Degree of protection=IP66", "Mechanical:Material=Sheet steel, RAL 7035"],
            Shape = Shape.Enclosure,
            WeightKg = 10m,
            BoxCm = (62m, 42m, 24m),
            Popularity = 0.9
        };

        yield return new Family
        {
            Leaf = "polycarbonate-stainless-enclosures",
            Brand = "Rittal",
            Series = "KL / Stainless",
            NounEn = "Enclosure",
            NounBn = "এনক্লোজার",
            Header = "Model|Material|Size|Price",
            Rows =
            [
                "KL polycarbonate junction box, small|Polycarbonate|150 × 150 × 80 mm|2650",
                "KL polycarbonate junction box, medium|Polycarbonate|254 × 254 × 111 mm|5250",
                "Stainless steel enclosure 304, small|AISI 304 stainless|300 × 300 × 150 mm|16500",
                "Stainless steel enclosure 304, medium|AISI 304 stainless|400 × 300 × 200 mm|26500",
                "Stainless steel enclosure 316, medium|AISI 316 stainless|400 × 300 × 200 mm|38500"
            ],
            Title = "{Brand} {Model}, {Size}",
            Intro = "A {Material} enclosure, {Size}, for corrosive or wash-down environments such as ETP plants, food processing and chemical areas. Sealed lid with captive screws and IP66 rating.",
            Highlights = ["{Material}, {Size}", "IP66 sealed", "Corrosion resistant", "Mounting lugs included"],
            Applications = ["ETP and water-treatment plants", "Food and beverage", "Chemical plants"],
            Specs = ["General:Model=Description", "Mechanical:Material=Material;Size=Dimensions"],
            Fixed = ["Environment:Degree of protection=IP66"],
            Shape = Shape.Enclosure,
            WeightKg = 3m,
            BoxCm = (42m, 32m, 18m),
            Popularity = 0.6
        };

        yield return new Family
        {
            Leaf = "terminal-blocks",
            Brand = "Phoenix Contact",
            Series = "CLIPLINE UT / PT",
            NounEn = "Terminal Block",
            NounBn = "টার্মিনাল ব্লক",
            Header = "Model|MPN|Section|Rating|Price",
            Rows =
            [
                "UT 2,5|3044076|0.14–4 mm²|24 A / 800 V|38",
                "UT 4|3044102|0.14–6 mm²|32 A / 800 V|52",
                "UT 6|3044131|0.2–10 mm²|41 A / 800 V|78",
                "UT 10|3044160|0.5–16 mm²|57 A / 1000 V|112",
                "UT 16|3044199|1.5–25 mm²|76 A / 1000 V|165",
                "PT 2,5|3208100|0.14–4 mm² (push-in)|24 A / 800 V|52",
                "PT 4|3211813|0.2–6 mm² (push-in)|32 A / 800 V|68"
            ],
            Title = "{Brand} {Model} Feed-through Terminal Block ({MPN}), {Section}",
            Intro = "The {Model} ({MPN}) is a screw- or push-in feed-through terminal block for {Section} conductors rated {Rating}. Grey, 35 mm DIN-rail mounting; bridge and marker accessories are sold separately.",
            Highlights = ["{Section} conductor range", "{Rating}", "35 mm DIN-rail (NS 35)", "Compatible with Phoenix Contact bridges and markers"],
            Applications = ["Control panel wiring", "Field wiring termination"],
            Specs = ["General:MPN=Order no.;Model=Model", "Electrical:Section=Conductor cross-section;Rating=Rated current / voltage"],
            Fixed = ["Environment:Temperature=-60 to 110 °C", "Mechanical:Mounting=NS 35 DIN rail"],
            Shape = Shape.DinModule,
            WeightKg = 0.02m,
            BoxCm = (4m, 4m, 6m),
            Popularity = 1.5,
            Axes = ["Pack|10 pieces:10.00;50 pieces:46.00;100 pieces:88.00"]
        };

        yield return new Family
        {
            Leaf = "terminal-blocks",
            Brand = "WAGO",
            Series = "TOPJOB S",
            NounEn = "Terminal Block",
            NounBn = "টার্মিনাল ব্লক",
            Header = "Model|Section|Rating|Price",
            Rows =
            [
                "2002-1201|0.25–4 mm²|32 A / 800 V|42",
                "2004-1201|0.5–6 mm²|41 A / 800 V|58",
                "2006-1201|0.5–10 mm²|57 A / 1000 V|88",
                "280-901|0.08–4 mm²|32 A / 800 V|34",
                "281-901|0.2–6 mm²|41 A / 800 V|46",
                "282-901|0.2–10 mm²|57 A / 1000 V|72"
            ],
            Title = "{Brand} {Model} Spring-pressure Terminal Block, {Section}",
            Intro = "The WAGO {Model} is a spring-clamp feed-through terminal block for {Section} conductors rated {Rating}: vibration-proof, maintenance-free connections that need no re-tightening.",
            Highlights = ["{Section}, {Rating}", "Spring-pressure connection, vibration-proof", "Maintenance free", "35 mm DIN-rail mounting"],
            Applications = ["Panels exposed to vibration", "Quick-wiring jobs"],
            Specs = ["General:Model=Model", "Electrical:Section=Conductor cross-section;Rating=Rated current / voltage"],
            Fixed = ["Mechanical:Mounting=35 mm DIN rail", "Environment:Temperature=-60 to 105 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.02m,
            BoxCm = (4m, 4m, 6m),
            Popularity = 1.0,
            Axes = ["Pack|10 pieces:10.00;50 pieces:46.00;100 pieces:88.00"]
        };

        yield return new Family
        {
            Leaf = "din-rails-accessories",
            Brand = "Phoenix Contact",
            Series = "NS 35",
            NounEn = "DIN Rail / Accessory",
            NounBn = "ডিআইএন রেল / অ্যাক্সেসরি",
            Header = "Model|MPN|Description|Price",
            Rows =
            [
                "NS 35/7,5 PERF 2000MM|1206560|Perforated steel DIN rail 35 × 7.5 mm, 2 m|1450",
                "NS 35/15 PERF 2000MM|1201756|Perforated steel DIN rail 35 × 15 mm, 2 m|1950",
                "NS 35/7,5 UNPERF 2000MM|0801733|Unperforated steel DIN rail 35 × 7.5 mm, 2 m|1250",
                "CLIPFIX 35|3022218|End clamp, snap-on, 35 mm rail|98",
                "E/UK|1201442|End clamp, screw type|85"
            ],
            Title = "{Brand} {Model} ({MPN}) — {Description}",
            Intro = "{Description}. Phoenix Contact DIN rails and end clamps are the standard mounting hardware for terminals, relays, power supplies and PLC modules.",
            Highlights = ["{Description}", "Zinc-plated steel", "Order no. {MPN}"],
            Applications = ["Panel mounting rails", "Securing terminal strips"],
            Specs = ["General:MPN=Order no.;Model=Model;Description=Description"],
            Fixed = ["Mechanical:Material=Zinc-plated steel"],
            Shape = Shape.Cable,
            WeightKg = 0.3m,
            BoxCm = (60m, 5m, 3m),
            Popularity = 0.8
        };

        yield return new Family
        {
            Leaf = "cable-ducts-trunking",
            Brand = "Hager",
            Series = "Slotted Wiring Duct",
            NounEn = "Wiring Duct",
            NounBn = "ওয়্যারিং ডাক্ট",
            Header = "Model|Size|Price",
            Rows =
            [
                "25 × 25 mm slotted duct|25 × 25 mm|420",
                "25 × 40 mm slotted duct|25 × 40 mm|520",
                "40 × 40 mm slotted duct|40 × 40 mm|650",
                "40 × 60 mm slotted duct|40 × 60 mm|820",
                "60 × 60 mm slotted duct|60 × 60 mm|1050",
                "80 × 60 mm slotted duct|80 × 60 mm|1350"
            ],
            Title = "{Brand} {Model} (Grey PVC, 2 m Length with Cover)",
            Intro = "A grey halogen-free PVC slotted wiring duct, {Size}, 2 m length with snap-on cover. Slotted sides allow wires to exit anywhere along the run for tidy panel wiring.",
            Highlights = ["{Size}, 2 m length", "Halogen-free PVC, RAL 7030 grey", "Snap-on cover", "Flame retardant UL94 V-0"],
            Applications = ["Control panel wiring", "Machine cable routing"],
            Specs = ["General:Model=Model", "Mechanical:Size=Duct size (W × H)"],
            Fixed = ["Environment:Flammability=UL94 V-0"],
            Shape = Shape.Cable,
            WeightKg = 0.6m,
            BoxCm = (200m, 7m, 7m),
            Popularity = 0.8
        };

        yield return new Family
        {
            Leaf = "cable-glands-lugs",
            Brand = "Phoenix Contact",
            Series = "AI Ferrules",
            NounEn = "Insulated Ferrule",
            NounBn = "ইনসুলেটেড ফেরুল",
            Header = "Model|Section|Colour|Price",
            Rows =
            [
                "AI 0,5-8 WH|0.5 mm²|White|420",
                "AI 0,75-8 GY|0.75 mm²|Grey|450",
                "AI 1-8 RD|1.0 mm²|Red|480",
                "AI 1,5-8 BK|1.5 mm²|Black|520",
                "AI 2,5-8 BU|2.5 mm²|Blue|620",
                "AI 4-10 GY|4 mm²|Grey|850",
                "AI 6-12 YE|6 mm²|Yellow|1150"
            ],
            Title = "{Brand} {Model} Insulated Wire-end Ferrules {Section} ({Colour}), Pack of 100",
            Intro = "Phoenix Contact {Model} insulated ferrules for {Section} stranded conductors, colour-coded to DIN 46228-4 ({Colour}). Crimp with a suitable ferrule crimping tool for a gas-tight, vibration-proof termination. Pack of 100.",
            Highlights = ["{Section} conductor, {Colour} sleeve", "DIN 46228-4 colour code", "Electro-tin-plated copper", "Pack of 100"],
            Applications = ["Wire-end termination in panels", "Preventing strand fraying"],
            Specs = ["General:Model=Model", "Electrical:Section=Conductor size;Colour=Colour code"],
            Fixed = ["Mechanical:Material=Electro-tin-plated copper, PP insulation", "Mechanical:Pack size=100 pieces"],
            Shape = Shape.Fitting,
            WeightKg = 0.05m,
            BoxCm = (8m, 5m, 3m),
            Popularity = 1.1
        };

        yield return new Family
        {
            Leaf = "cable-glands-lugs",
            Brand = "Phoenix Contact",
            Series = "CG Cable Glands",
            NounEn = "Cable Gland",
            NounBn = "ক্যাবল গ্ল্যান্ড",
            Header = "Model|Thread|Cable range|Price",
            Rows =
            [
                "CG M12 nylon gland|M12 × 1.5|3–6.5 mm|65",
                "CG M16 nylon gland|M16 × 1.5|4.5–10 mm|85",
                "CG M20 nylon gland|M20 × 1.5|6–12 mm|105",
                "CG M25 nylon gland|M25 × 1.5|9–16 mm|165",
                "CG M32 nylon gland|M32 × 1.5|13–20 mm|240"
            ],
            Title = "{Brand} {Model}, Cable Ø {Cable range} (IP68)",
            Intro = "A polyamide cable gland with {Thread} thread for cables of {Cable range}, with sealing insert and strain relief, IP68. For entering cables into enclosures and junction boxes.",
            Highlights = ["{Thread} thread", "Cable Ø {Cable range}", "IP68 with locknut and gasket", "UV-stable polyamide"],
            Applications = ["Cable entry into panels and boxes"],
            Specs = ["General:Model=Model", "Mechanical:Thread=Thread;Cable range=Cable diameter range"],
            Fixed = ["Environment:Degree of protection=IP68", "Mechanical:Material=Polyamide 6"],
            Shape = Shape.Fitting,
            WeightKg = 0.03m,
            BoxCm = (5m, 4m, 4m),
            Popularity = 0.9,
            Axes = ["Pack|Single piece:1.00;Pack of 10:9.00;Pack of 50:42.00"]
        };
    }
}
