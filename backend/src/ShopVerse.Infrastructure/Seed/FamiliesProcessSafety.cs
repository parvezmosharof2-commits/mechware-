namespace ShopVerse.Infrastructure.Seed;

/// <summary>Roots 10–12: water & ETP instruments, process measurement, machine safety & test tools.</summary>
internal static class FamiliesProcessSafety
{
    public static IEnumerable<Family> All()
    {
        // ───────────────────────── 10. Water, ETP & Process Instruments ─────────────────────────
        yield return new Family
        {
            Leaf = "electromagnetic-flow-meters",
            Brand = "Endress+Hauser",
            Series = "Proline Promag W 400",
            NounEn = "Electromagnetic Flow Meter",
            NounBn = "ইলেকট্রোম্যাগনেটিক ফ্লো মিটার",
            Header = "Model|Size|Flow range|Price",
            Rows =
            [
                "Promag W 400 DN25|DN25 (1″)|0.5 – 35 m³/h|285000",
                "Promag W 400 DN50|DN50 (2″)|2 – 140 m³/h|312000",
                "Promag W 400 DN80|DN80 (3″)|5 – 360 m³/h|345000",
                "Promag W 400 DN100|DN100 (4″)|8 – 565 m³/h|378000",
                "Promag W 400 DN150|DN150 (6″)|18 – 1 270 m³/h|465000",
                "Promag W 400 DN200|DN200 (8″)|32 – 2 260 m³/h|585000"
            ],
            Title = "{Brand} {Series} Electromagnetic Flow Meter {Size}",
            Intro = "The Promag W 400 {Model} measures conductive liquids such as water, wastewater and sludge in lines of {Size}, with a typical range of {Flow range}. The 4-wire transmitter outputs 4–20 mA, pulse and Modbus; the flange sensor is lined with polyurethane or rubber for drinking water and effluent.",
            Highlights = ["{Size}, flow range {Flow range}", "±0.5 % of reading accuracy", "4–20 mA / pulse / Modbus RTU outputs", "No moving parts, no pressure drop"],
            Applications = ["Effluent and ETP inlet/outlet metering", "Water distribution", "Dosing and chemical feed lines"],
            Specs = ["General:Model=Model", "Process:Size=Nominal size;Flow range=Typical flow range"],
            Fixed = ["Electrical:Supply=100–240 V AC / 24 V DC", "Environment:Degree of protection=IP66/67 (compact)", "Process:Measured media=Conductive liquids (≥ 5 µS/cm)"],
            Shape = Shape.Meter,
            WeightKg = 14m,
            BoxCm = (40m, 30m, 30m),
            Popularity = 0.4,
            WarrantyMonths = 24,
            FilterSpecs = ["Nominal size"]
        };

        yield return new Family
        {
            Leaf = "electromagnetic-flow-meters",
            Brand = "Yokogawa",
            Series = "AXF Magnetic Flowmeter",
            NounEn = "Electromagnetic Flow Meter",
            NounBn = "ইলেকট্রোম্যাগনেটিক ফ্লো মিটার",
            Header = "Model|Size|Flow range|Price",
            Rows =
            [
                "AXF050G|DN50 (2″)|2 – 140 m³/h|298000",
                "AXF100G|DN100 (4″)|8 – 565 m³/h|365000",
                "AXF150G|DN150 (6″)|18 – 1 270 m³/h|448000"
            ],
            Title = "{Brand} {Model} Magnetic Flowmeter {Size} (Integral Type)",
            Intro = "The Yokogawa {Model} is an AXF-series magnetic flowmeter in a {Size} line, measuring {Flow range} with dual-frequency excitation that remains stable in slurry and sludge applications.",
            Highlights = ["{Size}, {Flow range}", "Dual-frequency excitation for noisy fluids", "4–20 mA HART / pulse output", "IP67 integral converter"],
            Applications = ["Wastewater and sludge metering", "Process water", "Chemical feed"],
            Specs = ["General:Model=Model", "Process:Size=Nominal size;Flow range=Typical flow range"],
            Fixed = ["Electrical:Supply=100–240 V AC / 24 V DC", "Environment:Degree of protection=IP67"],
            Shape = Shape.Meter,
            WeightKg = 13m,
            BoxCm = (40m, 30m, 30m),
            Popularity = 0.3,
            WarrantyMonths = 24,
            FilterSpecs = ["Nominal size"]
        };

        yield return new Family
        {
            Leaf = "ultrasonic-level-sensors",
            Brand = "Pepperl+Fuchs",
            Series = "UC-30GM",
            NounEn = "Ultrasonic Sensor",
            NounBn = "আল্ট্রাসনিক সেন্সর",
            Header = "Model|Range|Output|Price",
            Rows =
            [
                "UC500-30GM-IUR2-V15|30 – 500 mm|4–20 mA + relay|32500",
                "UC2000-30GM-IUR2-V15|80 – 2 000 mm|4–20 mA + relay|36500",
                "UC4000-30GM-IUR2-V15|200 – 4 000 mm|4–20 mA + relay|44500",
                "UC6000-30GM-IUR2-V15|350 – 6 000 mm|4–20 mA + relay|58500"
            ],
            Title = "{Brand} {Model} Ultrasonic Level Sensor, Range {Range}",
            Intro = "The Pepperl+Fuchs {Model} is a M30 threaded ultrasonic sensor measuring distances over {Range}, with 4–20 mA analog and a switching output. Mounted above an open tank or channel it gives continuous level readings of water and effluent without touching the medium.",
            Highlights = ["Range {Range}", "4–20 mA analog + switching output", "Teach-in via cable (no PC needed)", "IP67, M12 connector"],
            Applications = ["Tank and sump level", "Open-channel level", "Silo and hopper contents"],
            Specs = ["General:Model=Model", "Sensing:Range=Detection range", "Electrical:Output=Output"],
            Fixed = ["Electrical:Supply=10–30 V DC", "Environment:Degree of protection=IP67"],
            Shape = Shape.RoundSensor,
            WeightKg = 0.2m,
            BoxCm = (14m, 8m, 8m),
            Popularity = 0.6
        };

        yield return new Family
        {
            Leaf = "ultrasonic-level-sensors",
            Brand = "SICK",
            Series = "UM30",
            NounEn = "Ultrasonic Sensor",
            NounBn = "আল্ট্রাসনিক সেন্সর",
            Header = "Model|Range|Output|Price",
            Rows =
            [
                "UM30-214111|65 – 1 300 mm|4–20 mA / 0–10 V|34500",
                "UM30-213111|200 – 8 000 mm|4–20 mA / 0–10 V|52500"
            ],
            Title = "{Brand} {Model} Ultrasonic Distance Sensor, {Range}",
            Intro = "The SICK {Model} is a M30 cylindrical ultrasonic sensor for contactless distance and level measurement over {Range}, with temperature compensation and teach-in buttons.",
            Highlights = ["Range {Range}", "Analog {Output}", "Temperature compensation", "IP67"],
            Applications = ["Level measurement", "Roll-diameter measurement"],
            Specs = ["General:Model=Model", "Sensing:Range=Detection range", "Electrical:Output=Output"],
            Fixed = ["Electrical:Supply=12–30 V DC", "Environment:Degree of protection=IP67"],
            Shape = Shape.RoundSensor,
            WeightKg = 0.2m,
            BoxCm = (14m, 8m, 8m),
            Popularity = 0.4
        };

        yield return new Family
        {
            Leaf = "ph-orp-analysers",
            Brand = "Endress+Hauser",
            Series = "Memosens pH / ORP",
            NounEn = "pH / ORP Instrument",
            NounBn = "পিএইচ / ওআরপি ইন্সট্রুমেন্ট",
            Header = "Model|Function|Price",
            Rows =
            [
                "CPS11D pH sensor|Digital Memosens glass pH sensor for process water|62500",
                "CPS12D ORP sensor|Digital Memosens ORP / redox sensor|58500",
                "CPS41D pH sensor|Digital pH sensor for high-pressure and temperature use|92500",
                "Liquiline CM442 transmitter|2-channel multi-parameter transmitter (4-wire)|215000",
                "Liquiline CM444 transmitter|4-channel multi-parameter transmitter (4-wire)|285000"
            ],
            Title = "{Brand} {Model} — {Function}",
            Intro = "The {Model}: {Function}. Memosens digital technology moves calibration to the lab (sensor heads are pre-calibrated and hot-swappable), cutting downtime and measurement error on ETP neutralisation and process-water control loops.",
            Highlights = ["{Function}", "Memosens inductive digital connection", "Pre-calibration in the lab, hot-swap in the field"],
            Applications = ["ETP neutralisation tank pH control", "Process water", "Cooling-water ORP monitoring"],
            Specs = ["General:Model=Model;Function=Description"],
            Fixed = ["Environment:Degree of protection=IP68 (sensors)"],
            Shape = Shape.Gauge,
            WeightKg = 0.5m,
            BoxCm = (28m, 12m, 12m),
            Popularity = 0.4,
            WarrantyMonths = 12
        };

        yield return new Family
        {
            Leaf = "ph-orp-analysers",
            Brand = "Yokogawa",
            Series = "FLXA21",
            NounEn = "Analyser Transmitter",
            NounBn = "অ্যানালাইজার ট্রান্সমিটার",
            Header = "Model|Function|Price",
            Rows =
            [
                "FLXA21 pH / ORP analyser|2-wire pH, ORP and conductivity analyser with display|168000",
                "FU20 pH sensor|General-purpose pH sensor, 3 m cable|44500"
            ],
            Title = "{Brand} {Model} — {Function}",
            Intro = "{Model}: {Function}. Yokogawa's FLXA21 analyser handles pH, ORP, conductivity and DO sensors on a 2-wire loop; FU20 is a rugged general-purpose pH electrode for wastewater.",
            Highlights = ["{Function}", "Loop-powered, 4–20 mA HART", "Backlit touch display"],
            Applications = ["ETP effluent monitoring", "Water treatment"],
            Specs = ["General:Model=Model;Function=Description"],
            Fixed = ["Environment:Degree of protection=IP66 (analyser)"],
            Shape = Shape.Gauge,
            WeightKg = 1.2m,
            BoxCm = (28m, 20m, 14m),
            Popularity = 0.3
        };

        yield return new Family
        {
            Leaf = "conductivity-do-analysers",
            Brand = "Endress+Hauser",
            Series = "Memosens CLS / COS",
            NounEn = "Conductivity / DO Sensor",
            NounBn = "কন্ডাক্টিভিটি / ডিও সেন্সর",
            Header = "Model|Function|Price",
            Rows =
            [
                "CLS15D conductivity sensor|Digital conductive sensor for pure and ultra-pure water|72500",
                "CLS21D conductivity sensor|Inductive digital conductivity sensor for high-conductivity fluids|88500",
                "COS22D DO sensor|Digital amperometric dissolved-oxygen sensor|96500",
                "COS61D DO sensor|Digital optical dissolved-oxygen sensor|128500"
            ],
            Title = "{Brand} {Model} — {Function}",
            Intro = "The {Model}: {Function}. Fits the Liquiline CM44x family via Memosens digital connection. Dissolved-oxygen sensing is critical on aeration tanks in effluent treatment to control blower speed and power use.",
            Highlights = ["{Function}", "Memosens digital connection", "Compatible with Liquiline CM442/CM444"],
            Applications = ["Aeration tank DO control", "RO permeate and boiler feed water conductivity", "Cooling water"],
            Specs = ["General:Model=Model;Function=Description"],
            Fixed = ["Environment:Degree of protection=IP68"],
            Shape = Shape.Gauge,
            WeightKg = 0.4m,
            BoxCm = (28m, 10m, 10m),
            Popularity = 0.3
        };

        yield return new Family
        {
            Leaf = "dosing-pumps",
            Brand = "Grundfos",
            Series = "DDA / DDC",
            NounEn = "Dosing Pump",
            NounBn = "ডোজিং পাম্প",
            Header = "Model|Capacity|Pressure|Price",
            Rows =
            [
                "DDA 7.5-16 AR-PP/V/C|7.5 l/h|16 bar|98500",
                "DDA 12-10 AR-PP/V/C|12 l/h|10 bar|98500",
                "DDA 17-7 AR-PP/V/C|17 l/h|7 bar|98500",
                "DDA 30-4 AR-PP/V/C|30 l/h|4 bar|105000",
                "DDC 6-10 AR-PP/V/C|6 l/h|10 bar|76500"
            ],
            Title = "{Brand} {Model} Digital Dosing Pump, {Capacity} / {Pressure}",
            Intro = "The Grundfos {Model} is a diaphragm dosing pump with {Capacity} maximum capacity at {Pressure}, a built-in stepper motor and a 4–20 mA / pulse-controlled digital interface. Used for chemical dosing (hypochlorite, coagulant, polymer, acid/alkali) in water and effluent plants.",
            Highlights = ["{Capacity} at {Pressure}", "Adjustable 1:3000 turn-down ratio", "4–20 mA / pulse / manual control", "Built-in FlowControl and air-lock detection"],
            Applications = ["ETP chemical dosing", "Boiler and cooling-water treatment", "Disinfection"],
            Specs = ["General:Model=Model", "Process:Capacity=Max capacity;Pressure=Max pressure"],
            Fixed = ["Electrical:Supply=100–240 V AC, 50/60 Hz", "Process:Wetted parts=PP / FKM / ceramic"],
            Shape = Shape.Pump,
            WeightKg = 3.2m,
            BoxCm = (32m, 22m, 20m),
            Popularity = 0.6,
            WarrantyMonths = 24
        };

        yield return new Family
        {
            Leaf = "submersible-booster-pumps",
            Brand = "Grundfos",
            Series = "JP / CM / Unilift",
            NounEn = "Pump",
            NounBn = "পাম্প",
            Header = "Model|Type|Power|Max flow|Price",
            Rows =
            [
                "JP 5|Self-priming jet pump|0.9 kW|5.4 m³/h|42500",
                "JP 6|Self-priming jet pump|1.1 kW|6.8 m³/h|48500",
                "CM 3-4|Horizontal multistage booster|0.75 kW|4.5 m³/h|68500",
                "CM 5-4|Horizontal multistage booster|1.1 kW|6.5 m³/h|78500",
                "Unilift KP 150-A1|Submersible drainage|0.75 kW|15 m³/h|36500",
                "Unilift AP 12.40.04.1|Submersible wastewater|0.9 kW|18 m³/h|62500"
            ],
            Title = "{Brand} {Model} — {Type}, {Power}",
            Intro = "The Grundfos {Model} is a {Type} pump rated {Power} with up to {Max flow} flow. Suitable for water supply, pressure boosting, drainage and wastewater duty depending on the version.",
            Highlights = ["{Type}, {Power}", "Maximum flow {Max flow}", "Stainless-steel wetted parts (CM)", "Thermal motor protection"],
            Applications = ["Pressure boosting", "Sump and drainage", "Wastewater lift stations"],
            Specs = ["General:Model=Model;Type=Pump type", "Mechanical:Power=Motor power;Max flow=Maximum flow"],
            Fixed = ["Electrical:Voltage / frequency=1 × 230 V, 50 Hz (3 × 400 V on request)"],
            Shape = Shape.Pump,
            WeightKg = 14m,
            BoxCm = (50m, 30m, 30m),
            Popularity = 0.7,
            WarrantyMonths = 24,
            FilterSpecs = ["Motor power"]
        };

        // ───────────────────────── 11. Process Measurement ─────────────────────────
        yield return new Family
        {
            Leaf = "pressure-transmitters",
            Brand = "Danfoss",
            Series = "MBS 3000",
            NounEn = "Pressure Transmitter",
            NounBn = "প্রেশার ট্রান্সমিটার",
            Header = "Model|Range|Output|Price",
            Rows =
            [
                "MBS 3000 0–6 bar|0–6 bar|4–20 mA|14500",
                "MBS 3000 0–10 bar|0–10 bar|4–20 mA|14500",
                "MBS 3000 0–16 bar|0–16 bar|4–20 mA|14500",
                "MBS 3000 0–25 bar|0–25 bar|4–20 mA|14500",
                "MBS 3000 0–40 bar|0–40 bar|4–20 mA|14500",
                "MBS 3000 0–100 bar|0–100 bar|4–20 mA|16500"
            ],
            Title = "{Brand} {Series} Pressure Transmitter {Range}, {Output}",
            Intro = "The Danfoss {Model} is a stainless-steel pressure transmitter with {Range} range and {Output} output. The all-welded sensor is used on hydraulics, pneumatics, water systems and refrigeration plants; G1/4 process connection and DIN connector.",
            Highlights = ["Range {Range}, {Output}", "Stainless-steel welded sensor", "±0.5 % FS accuracy", "G1/4 process connection"],
            Applications = ["Pump discharge pressure", "Hydraulic power packs", "Compressors and refrigeration"],
            Specs = ["General:Model=Model", "Process:Range=Pressure range;Output=Output"],
            Fixed = ["Electrical:Supply=10–30 V DC", "Environment:Degree of protection=IP65", "Process:Connection=G1/4 male"],
            Shape = Shape.Gauge,
            WeightKg = 0.14m,
            BoxCm = (10m, 6m, 6m),
            Popularity = 1.2,
            FilterSpecs = ["Pressure range"]
        };

        yield return new Family
        {
            Leaf = "pressure-transmitters",
            Brand = "Endress+Hauser",
            Series = "Cerabar PMC11",
            NounEn = "Pressure Transmitter",
            NounBn = "প্রেশার ট্রান্সমিটার",
            Header = "Model|Range|Price",
            Rows =
            [
                "Cerabar PMC11 0–1 bar|0–1 bar|46500",
                "Cerabar PMC11 0–10 bar|0–10 bar|46500",
                "Cerabar PMC11 0–40 bar|0–40 bar|48500"
            ],
            Title = "{Brand} {Series} Ceramic Pressure Transmitter {Range}, 4–20 mA",
            Intro = "The Cerabar PMC11 {Model} uses a dry ceramic Ceraphire sensor for absolute overload resistance and long-term stability on water, wastewater and gas. Output 4–20 mA, process connection G½.",
            Highlights = ["Range {Range}", "Ceramic sensor, high overload resistance", "4–20 mA output", "Wetted parts compatible with water/air/oil"],
            Applications = ["Water and wastewater pressure", "Filtration monitoring"],
            Specs = ["General:Model=Model", "Process:Range=Pressure range"],
            Fixed = ["Electrical:Supply=10–30 V DC", "Environment:Degree of protection=IP65 / IP67"],
            Shape = Shape.Gauge,
            WeightKg = 0.2m,
            BoxCm = (11m, 7m, 7m),
            Popularity = 0.6,
            FilterSpecs = ["Pressure range"]
        };

        yield return new Family
        {
            Leaf = "pressure-switches",
            Brand = "Danfoss",
            Series = "KP Pressure Switches",
            NounEn = "Pressure Switch",
            NounBn = "প্রেশার সুইচ",
            Header = "Model|Application|Range|Price",
            Rows =
            [
                "KP 1|Low pressure, refrigeration|-0.2 – 7.5 bar|7450",
                "KP 5|High pressure, refrigeration|8 – 32 bar|7950",
                "KP 15|Combined LP/HP|-0.9 – 7 bar (LP) / 8–32 bar (HP)|9850",
                "KP 35|Low pressure, with manual reset|-0.2 – 8 bar|8250",
                "KP 36|Low pressure, automatic reset|-0.2 – 8 bar|8250"
            ],
            Title = "{Brand} {Model} Pressure Switch, {Application}",
            Intro = "The Danfoss {Model} is a {Application} pressure switch with {Range} range, SPDT contact and adjustable differential. Used for compressor and pump protection on chillers, air compressors and process lines.",
            Highlights = ["{Application}", "Range {Range}", "SPDT contact, adjustable differential", "IP30 / IP54 options"],
            Applications = ["Compressor protection", "Chiller control", "Pump dry-run / overpressure cut-out"],
            Specs = ["General:Model=Model;Application=Application", "Process:Range=Adjustment range"],
            Fixed = ["Electrical:Contact=SPDT, 16 A / 400 V AC"],
            Shape = Shape.Gauge,
            WeightKg = 0.3m,
            BoxCm = (12m, 8m, 8m),
            Popularity = 0.7
        };

        yield return new Family
        {
            Leaf = "rtd-thermocouple-sensors",
            Brand = "Endress+Hauser",
            Series = "TR / TC Probes",
            NounEn = "Temperature Probe",
            NounBn = "তাপমাত্রা প্রোব",
            Header = "Model|Type|Length|Price",
            Rows =
            [
                "Pt100 Class A probe Ø6 × 100 mm|Pt100 RTD, 3-wire|100 mm|9850",
                "Pt100 Class A probe Ø6 × 150 mm|Pt100 RTD, 3-wire|150 mm|10500",
                "Pt100 Class A probe Ø6 × 250 mm|Pt100 RTD, 3-wire|250 mm|11500",
                "K-type thermocouple Ø6 × 150 mm|Type K thermocouple|150 mm|8250",
                "K-type thermocouple Ø6 × 300 mm|Type K thermocouple|300 mm|9250",
                "J-type thermocouple Ø6 × 150 mm|Type J thermocouple|150 mm|8250"
            ],
            Title = "{Brand} {Model}, Stainless Steel Sheath",
            Intro = "A stainless-steel sheathed {Type} probe of {Length} immersion length with ½″ BSP adjustable compression fitting and terminal head. Pt100 probes give accuracy and stability for process control; thermocouples cover higher temperatures.",
            Highlights = ["{Type}, {Length} immersion", "316 stainless sheath, Ø6 mm", "Terminal head with ceramic block", "Class A accuracy (RTD)"],
            Applications = ["Boiler and oven temperature", "Process tanks", "HVAC ducts and pipes"],
            Specs = ["General:Model=Model", "Sensing:Type=Sensor type;Length=Immersion length"],
            Fixed = ["Environment:Degree of protection=IP65 (head)"],
            Shape = Shape.Cable,
            WeightKg = 0.2m,
            BoxCm = (30m, 8m, 8m),
            Popularity = 0.8
        };

        yield return new Family
        {
            Leaf = "temperature-controllers",
            Brand = "Omron",
            Series = "E5CC / E5EC / E5AC",
            NounEn = "Temperature Controller",
            NounBn = "টেম্পারেচার কন্ট্রোলার",
            Header = "Model|Size|Output|Price",
            Rows =
            [
                "E5CC-RX2ASM-800|48 × 48 mm|Relay|13500",
                "E5CC-QX2ASM-800|48 × 48 mm|Voltage (SSR drive)|13500",
                "E5CC-CX2ASM-800|48 × 48 mm|Current (4–20 mA)|15500",
                "E5EC-RX2ASM-800|48 × 96 mm|Relay|16500",
                "E5AC-RX2ASM-800|96 × 96 mm|Relay|18500"
            ],
            Title = "{Brand} {Model} Digital Temperature Controller {Size}, {Output} Output",
            Intro = "The Omron {Model} is a digital PID temperature controller ({Size}) with {Output} control output, universal thermocouple/RTD input and high-visibility 11-segment display. Auto-tuning and 50 ms sampling give precise control on ovens, moulds and heaters.",
            Highlights = ["{Size} DIN size, {Output} output", "Universal TC/RTD input", "Auto-tuning PID, 50 ms sampling", "Push-in terminals option"],
            Applications = ["Ovens and dryers", "Plastic injection and extrusion heaters", "Sealing bars"],
            Specs = ["General:Model=Model", "Electrical:Size=Panel size;Output=Control output"],
            Fixed = ["Electrical:Supply=100–240 V AC", "Sensing:Input=Thermocouple (K, J, T, E, R, S, B…), Pt100"],
            Shape = Shape.Meter,
            WeightKg = 0.25m,
            BoxCm = (14m, 10m, 10m),
            Popularity = 1.4,
            FilterSpecs = ["Panel size"]
        };

        yield return new Family
        {
            Leaf = "temperature-controllers",
            Brand = "Autonics",
            Series = "TK / TZN",
            NounEn = "Temperature Controller",
            NounBn = "টেম্পারেচার কন্ট্রোলার",
            Header = "Model|Size|Output|Price",
            Rows =
            [
                "TK4S-14RN|48 × 48 mm|Relay|7450",
                "TK4M-14RN|72 × 72 mm|Relay|9250",
                "TZN4S-14R|48 × 48 mm|Relay|6850",
                "TZN4M-14R|72 × 72 mm|Relay|8450"
            ],
            Title = "{Brand} {Model} PID Temperature Controller {Size}",
            Intro = "The Autonics {Model} is a PID temperature controller ({Size}) with relay control output, dual-display and auto-tune. A budget-friendly option for ovens, incubators and process heaters.",
            Highlights = ["{Size}, relay output", "Universal TC/RTD input", "PID auto-tune", "Dual-line display"],
            Applications = ["Ovens and heaters", "Incubators", "Packaging sealing"],
            Specs = ["General:Model=Model", "Electrical:Size=Panel size;Output=Control output"],
            Fixed = ["Electrical:Supply=100–240 V AC"],
            Shape = Shape.Meter,
            WeightKg = 0.18m,
            BoxCm = (12m, 10m, 10m),
            Popularity = 1.5,
            FilterSpecs = ["Panel size"]
        };

        yield return new Family
        {
            Leaf = "temperature-controllers",
            Brand = "Hanyoung Nux",
            Series = "NX Series",
            NounEn = "Temperature Controller",
            NounBn = "টেম্পারেচার কন্ট্রোলার",
            Header = "Model|Size|Output|Price",
            Rows =
            [
                "NX4-00|48 × 48 mm|Relay|6450",
                "NX7-00|72 × 72 mm|Relay|7850",
                "NX9-00|96 × 96 mm|Relay|9250"
            ],
            Title = "{Brand} {Model} PID Temperature Controller {Size}",
            Intro = "The Hanyoung Nux {Model} is a general-purpose digital PID temperature controller in a {Size} housing with relay output and universal input.",
            Highlights = ["{Size}, relay output", "PID with auto-tune", "Universal input"],
            Applications = ["Heating equipment", "Ovens"],
            Specs = ["General:Model=Model", "Electrical:Size=Panel size;Output=Control output"],
            Fixed = ["Electrical:Supply=100–240 V AC"],
            Shape = Shape.Meter,
            WeightKg = 0.2m,
            BoxCm = (12m, 10m, 10m),
            Popularity = 0.8,
            FilterSpecs = ["Panel size"]
        };

        yield return new Family
        {
            Leaf = "digital-panel-meters",
            Brand = "Autonics",
            Series = "MT4W",
            NounEn = "Digital Panel Meter",
            NounBn = "ডিজিটাল প্যানেল মিটার",
            Header = "Model|Measures|Price",
            Rows =
            [
                "MT4W-DV-4N|DC voltmeter|5850",
                "MT4W-DA-4N|DC ammeter|5850",
                "MT4W-AV-4N|AC voltmeter|6450",
                "MT4W-AA-4N|AC ammeter|6450"
            ],
            Title = "{Brand} {Model} 72 × 36 mm 4-digit {Measures}",
            Intro = "The Autonics {Model} is a 4-digit {Measures} in a 72 × 36 mm panel housing with a bright LED display and selectable input range. Used to display voltage or current on machine panels and power supplies.",
            Highlights = ["{Measures}, 4-digit LED", "72 × 36 mm panel cutout", "Selectable range", "Simple front-panel setup"],
            Applications = ["Panel voltage / current indication", "Power supply monitoring"],
            Specs = ["General:Model=Model", "Electrical:Measures=Measured quantity"],
            Fixed = ["Electrical:Supply=100–240 V AC / 24 V DC"],
            Shape = Shape.Meter,
            WeightKg = 0.12m,
            BoxCm = (10m, 9m, 9m),
            Popularity = 0.7
        };

        yield return new Family
        {
            Leaf = "counters-totalizers",
            Brand = "Omron",
            Series = "H7 Series",
            NounEn = "Counter / Hour Meter",
            NounBn = "কাউন্টার / আওয়ার মিটার",
            Header = "Model|Function|Digits|Price",
            Rows =
            [
                "H7CX-A114|Multi-function counter/timer/tachometer|4/6-digit|13500",
                "H7ET-N|Hour meter (digital)|8-digit|3450",
                "H7EC-N|Total counter|8-digit|2850",
                "H7CC-A11|Preset counter|6-digit|7850"
            ],
            Title = "{Brand} {Model} {Function} ({Digits})",
            Intro = "The Omron {Model} is a {Function} with {Digits} display. Panel-mounted, simple to configure, and used on packaging machines for batch counting and on motors for maintenance hour-metering.",
            Highlights = ["{Function}", "Display {Digits}", "Panel mount", "Long-life backup"],
            Applications = ["Batch counting", "Motor / machine run-hours", "Speed display"],
            Specs = ["General:Model=Model;Function=Function", "Display:Digits=Digits"],
            Fixed = ["Electrical:Supply=100–240 V AC / 12–24 V DC (model dependent)"],
            Shape = Shape.Meter,
            WeightKg = 0.1m,
            BoxCm = (10m, 9m, 9m),
            Popularity = 0.8
        };

        yield return new Family
        {
            Leaf = "counters-totalizers",
            Brand = "Autonics",
            Series = "CT6",
            NounEn = "Counter / Timer",
            NounBn = "কাউন্টার / টাইমার",
            Header = "Model|Function|Price",
            Rows =
            [
                "CT6S-1P4|1-stage preset counter|5250",
                "CT6S-2P4|2-stage preset counter|5850",
                "CT6M-1P4|1-stage counter, 72 × 72 mm|6450",
                "CT6Y-1P4|1-stage counter, 72 × 72 mm (large display)|7050"
            ],
            Title = "{Brand} {Model} {Function}",
            Intro = "The Autonics {Model} is a {Function} with 6-digit LCD display, high-speed input up to 5 kcps and DIP-switch setup. A flexible choice for batch and length counting.",
            Highlights = ["{Function}", "6-digit display", "Selectable input modes", "Relay or transistor outputs"],
            Applications = ["Batch counting", "Length counting"],
            Specs = ["General:Model=Model;Function=Function"],
            Fixed = ["Electrical:Supply=100–240 V AC / 24 V DC"],
            Shape = Shape.Meter,
            WeightKg = 0.15m,
            BoxCm = (10m, 9m, 9m),
            Popularity = 0.7
        };

        // ───────────────────────── 12. Machine Safety & Test Tools ─────────────────────────
        yield return new Family
        {
            Leaf = "emergency-stop-safety-switches",
            Brand = "Schneider Electric",
            Series = "Preventa / Harmony",
            NounEn = "Safety Switch",
            NounBn = "সেফটি সুইচ",
            Header = "Model|Function|Contacts|Price",
            Rows =
            [
                "XB5AS8445|Ø22 mm emergency stop, turn-to-release, red mushroom Ø40|1 NC + 1 NO|1750",
                "XB4BS8445|Ø22 mm metal emergency stop, turn-to-release|1 NC + 1 NO|2450",
                "XCSA502|Preventa safety limit switch (hinged guard)|1 NC + 1 NO|5850",
                "XCSPA591|Preventa safety interlock with key|2 NC|7450",
                "XY2CE1A250|Emergency stop rope-pull switch|2 NC + 1 NO|18500"
            ],
            Title = "{Brand} {Model} — {Function}",
            Intro = "The {Model}: {Function}, {Contacts}. Positive-opening contacts (IEC 60947-5-1) and robust construction make these devices suitable for machine guarding and emergency-stop circuits.",
            Highlights = ["{Function}", "Contacts {Contacts}", "Positive-opening contacts", "IP66 / IP67"],
            Applications = ["Emergency stop circuits", "Guard-door interlocks", "Conveyor rope-pull"],
            Specs = ["General:Model=Model;Function=Function", "Electrical:Contacts=Contacts"],
            Fixed = ["Environment:Degree of protection=IP66 / IP67"],
            Shape = Shape.Pushbutton,
            WeightKg = 0.2m,
            BoxCm = (10m, 8m, 8m),
            Popularity = 0.9
        };

        yield return new Family
        {
            Leaf = "safety-relays-light-curtains",
            Brand = "Pilz",
            Series = "PNOZ",
            NounEn = "Safety Relay",
            NounBn = "সেফটি রিলে",
            Header = "Model|MPN|Function|Price",
            Rows =
            [
                "PNOZ s3 24 VDC|750103|E-stop / guard monitoring, 3 safety contacts|36500",
                "PNOZ s4 24 VDC|750104|Safety relay, 3 safety contacts + 1 aux|39500",
                "PNOZ s5 24 VDC|750105|Safety relay, 3 safety contacts, extra output|42500",
                "PNOZ X3 24 VDC/24 VAC|777303|Multi-function safety relay, 3 safety contacts|34500",
                "PNOZ s30 24–240 V|751330|Safety relay with timed + instantaneous contacts|58500"
            ],
            Title = "{Brand} {Model} ({MPN}) — {Function}",
            Intro = "The Pilz {Model} (order no. {MPN}) is a safety relay for {Function}. Suitable for applications up to SIL 3 / PL e, it monitors emergency stops, guard doors and light barriers.",
            Highlights = ["{Function}", "Up to SIL 3 / PL e", "Order no. {MPN}", "Pluggable terminals (s-series)"],
            Applications = ["Emergency-stop monitoring", "Guard-door monitoring", "Press and packaging safety circuits"],
            Specs = ["General:MPN=Order no.;Model=Model;Function=Function"],
            Fixed = ["Environment:Degree of protection=IP20"],
            Shape = Shape.DinModule,
            WeightKg = 0.25m,
            BoxCm = (11m, 8m, 9m),
            Popularity = 0.6
        };

        yield return new Family
        {
            Leaf = "digital-multimeters",
            Brand = "Fluke",
            Series = "Digital Multimeters",
            NounEn = "Digital Multimeter",
            NounBn = "ডিজিটাল মাল্টিমিটার",
            Header = "Model|Class|Accuracy|Price",
            Rows =
            [
                "101|Pocket|0.5 % DCV|7850",
                "106|Handheld|0.5 % DCV|12500",
                "15B+|Handheld|0.5 % DCV|9850",
                "17B+|Handheld with temperature|0.5 % DCV|15850",
                "115|True-rms field service|0.5 % DCV|24500",
                "117|True-rms electrician (non-contact voltage)|0.5 % DCV|31500",
                "179|True-rms with temperature|0.09 % DCV|40500",
                "87V|True-rms industrial|0.05 % DCV|56500"
            ],
            Title = "{Brand} {Model} Digital Multimeter, {Class}",
            Intro = "The {Model} is a {Class} digital multimeter from Fluke with {Accuracy} basic DC accuracy. A trusted tool for maintenance technicians, electricians and commissioning engineers.",
            Highlights = ["{Class}", "Basic DC accuracy {Accuracy}", "CAT III 600 V (CAT IV on higher models)", "Includes test leads and batteries"],
            Applications = ["Electrical troubleshooting", "Commissioning and maintenance"],
            Specs = ["General:Model=Model;Class=Type", "Electrical:Accuracy=Basic DC accuracy"],
            Fixed = ["Safety:Category=CAT III 600 V or better"],
            Shape = Shape.Meter,
            WeightKg = 0.4m,
            BoxCm = (20m, 12m, 7m),
            Popularity = 1.2,
            WarrantyMonths = 36
        };

        yield return new Family
        {
            Leaf = "clamp-insulation-testers",
            Brand = "Fluke",
            Series = "Clamp Meters & Insulation Testers",
            NounEn = "Clamp Meter / Insulation Tester",
            NounBn = "ক্ল্যাম্প মিটার / ইনসুলেশন টেস্টার",
            Header = "Model|Type|Rating|Price",
            Rows =
            [
                "317|Clamp meter, AC/DC|600 A|18500",
                "323|True-rms AC clamp meter|400 A AC|21500",
                "324|True-rms AC clamp meter with temperature|400 A AC|27500",
                "325|True-rms AC/DC clamp meter|400 A AC/DC|34500",
                "376 FC|True-rms AC/DC clamp meter, wireless|1000 A AC/DC|74500",
                "1507|Insulation tester|500 V – 1 kV, 10 GΩ|88500",
                "1587 FC|Insulation multimeter, wireless|50 V – 1 kV, 2 GΩ|128000"
            ],
            Title = "{Brand} {Model} — {Type}, {Rating}",
            Intro = "The Fluke {Model} is a {Type} rated {Rating}. Clamp meters measure current without breaking the circuit; insulation testers verify motor, cable and transformer insulation resistance before commissioning.",
            Highlights = ["{Type}", "Rating {Rating}", "CAT III 600 V / CAT IV 300 V or better", "Carry case and leads included"],
            Applications = ["Motor current checks", "Insulation resistance testing", "Preventive maintenance"],
            Specs = ["General:Model=Model;Type=Type", "Electrical:Rating=Measurement range"],
            Fixed = ["Safety:Category=CAT III 600 V or better"],
            Shape = Shape.Meter,
            WeightKg = 0.5m,
            BoxCm = (28m, 14m, 8m),
            Popularity = 0.8,
            WarrantyMonths = 36
        };

        yield return new Family
        {
            Leaf = "enclosure-fans-filters",
            Brand = "Rittal",
            Series = "SK Fan-and-Filter Units",
            NounEn = "Fan-and-Filter Unit",
            NounBn = "ফ্যান-অ্যান্ড-ফিল্টার ইউনিট",
            Header = "Model|Airflow|Cutout|Price",
            Rows =
            [
                "SK 3237|20 m³/h (free blowing)|92 × 92 mm|6850",
                "SK 3238|55 m³/h (free blowing)|125 × 125 mm|8450",
                "SK 3239|105 m³/h (free blowing)|204 × 204 mm|11500",
                "SK 3240|180 m³/h (free blowing)|255 × 255 mm|14500",
                "SK 3243|550 m³/h (free blowing)|323 × 323 mm|28500"
            ],
            Title = "{Brand} {Model} Fan-and-Filter Unit, {Airflow}",
            Intro = "The Rittal {Model} fan-and-filter unit moves {Airflow} through the enclosure to remove heat from drives and power supplies. Delivered with a washable filter mat; pair with an exhaust filter on the opposite side.",
            Highlights = ["Airflow {Airflow}", "Cutout {Cutout}", "IP54 with filter mat", "Washable filter"],
            Applications = ["Cooling VFD and PLC panels", "Heat removal from power supplies"],
            Specs = ["General:Model=Model", "Mechanical:Airflow=Airflow;Cutout=Panel cutout"],
            Fixed = ["Environment:Degree of protection=IP54"],
            Shape = Shape.Cylinder,
            WeightKg = 0.8m,
            BoxCm = (30m, 30m, 12m),
            Popularity = 0.7,
            Axes = ["Supply voltage|24 V DC:1.05;115 V AC:1.00;230 V AC:1.00"]
        };

        yield return new Family
        {
            Leaf = "panel-heaters-thermostats",
            Brand = "Rittal",
            Series = "SK Heaters & Thermostats",
            NounEn = "Panel Heater / Thermostat",
            NounBn = "প্যানেল হিটার / থার্মোস্ট্যাট",
            Header = "Model|Function|Rating|Price",
            Rows =
            [
                "SK heater 20 W|Anti-condensation heater|20 W|3450",
                "SK heater 45 W|Anti-condensation heater|45 W|4250",
                "SK heater 75 W|Anti-condensation heater|75 W|4850",
                "SK heater 150 W|Fan heater|150 W|9850",
                "SK 3110 thermostat NC|Enclosure thermostat, NC (0–60 °C)|10 A / 250 V|2850",
                "SK 3110 thermostat NO|Enclosure thermostat, NO (0–60 °C)|10 A / 250 V|2850"
            ],
            Title = "{Brand} {Model} — {Function}",
            Intro = "The Rittal {Model}: {Function}, {Rating}. Prevents condensation and maintains a safe temperature range inside enclosures installed in humid or outdoor locations.",
            Highlights = ["{Function}", "Rating {Rating}", "Snap-on DIN-rail mounting", "Touch-safe housing"],
            Applications = ["Preventing condensation in panels", "Frost protection of outdoor cabinets"],
            Specs = ["General:Model=Model;Function=Function", "Electrical:Rating=Rating"],
            Fixed = ["Electrical:Voltage=110–240 V AC (heaters)"],
            Shape = Shape.DinModule,
            WeightKg = 0.3m,
            BoxCm = (12m, 8m, 6m),
            Popularity = 0.6
        };
    }
}
