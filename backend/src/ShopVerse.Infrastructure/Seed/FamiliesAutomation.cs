namespace ShopVerse.Infrastructure.Seed;

/// <summary>Roots 1–3: PLCs & controllers, HMI & industrial PCs, drives & motion control.</summary>
internal static class FamiliesAutomation
{
    public static IEnumerable<Family> All()
    {
        // ───────────────────────── 1. PLCs & Controllers ─────────────────────────
        yield return new Family
        {
            Leaf = "compact-plcs",
            Brand = "Siemens",
            Series = "SIMATIC S7-1200",
            NounEn = "Compact PLC",
            NounBn = "কমপ্যাক্ট পিএলসি",
            Header = "Model|MPN|Supply|DI|DO|AI|Memory|Price",
            Rows =
            [
                "CPU 1211C DC/DC/DC|6ES7211-1AE40-0XB0|24 V DC|6|4 (24 V DC)|2|50 KB|38500",
                "CPU 1212C DC/DC/DC|6ES7212-1AE40-0XB0|24 V DC|8|6 (24 V DC)|2|75 KB|46500",
                "CPU 1212C AC/DC/Relay|6ES7212-1BE40-0XB0|85–264 V AC|8|6 (Relay)|2|75 KB|49500",
                "CPU 1214C DC/DC/DC|6ES7214-1AG40-0XB0|24 V DC|14|10 (24 V DC)|2|100 KB|62500",
                "CPU 1214C AC/DC/Relay|6ES7214-1BG40-0XB0|85–264 V AC|14|10 (Relay)|2|100 KB|66000",
                "CPU 1215C DC/DC/DC|6ES7215-1AG40-0XB0|24 V DC|14|10 (24 V DC)|2 (+2 AO)|125 KB|78500",
                "CPU 1217C DC/DC/DC|6ES7217-1AG40-0XB0|24 V DC|14|10 (24 V DC)|2 (+2 AO)|150 KB|98500"
            ],
            Title = "{Brand} {Series} {Model} Compact PLC ({MPN})",
            Intro = "The {Series} {Model} is a compact controller with integrated PROFINET, a high-speed counting and pulse-output engine, and room for up to three communication modules and eight signal modules. It is programmed in TIA Portal and suits standalone machines, packaging lines and utility skids.",
            Highlights = ["{Memory} work memory with integrated PROFINET port", "{DI} digital inputs and {DO} digital outputs onboard", "{AI} analog inputs (0–10 V) onboard, supply {Supply}", "Expandable with signal boards, signal modules and communication modules"],
            Applications = ["Standalone packaging and filling machines", "Pump and compressor control in utility rooms", "Conveyor and sorting lines", "Retrofit of relay-logic panels"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model", "Electrical:Supply=Supply voltage;DI=Digital inputs;DO=Digital outputs;AI=Analog inputs", "Memory:Memory=Work memory"],
            Fixed = ["General:Programming software=TIA Portal (STEP 7 Basic/Professional)", "Communication:Onboard interface=PROFINET (Ethernet), 2 ports on CPU 1215C/1217C", "Environment:Ambient temperature=-20 to 60 °C (horizontal mounting)", "Environment:Degree of protection=IP20"],
            Shape = Shape.DinModule,
            WarrantyMonths = 12,
            WeightKg = 0.43m,
            BoxCm = (15m, 11m, 9m),
            Popularity = 1.6,
            FilterSpecs = ["Supply voltage"]
        };

        yield return new Family
        {
            Leaf = "micro-nano-plcs",
            Brand = "Mitsubishi Electric",
            Series = "MELSEC FX3U",
            NounEn = "Micro PLC",
            NounBn = "মাইক্রো পিএলসি",
            Header = "Model|Inputs|Outputs|Output type|Price",
            Rows =
            [
                "FX3U-16MR/ES-A|8|8|Relay|32800",
                "FX3U-32MR/ES-A|16|16|Relay|44500",
                "FX3U-48MR/ES-A|24|24|Relay|58500",
                "FX3U-64MR/ES-A|32|32|Relay|71500",
                "FX3U-32MT/ES-A|16|16|Transistor (sink)|47500",
                "FX3U-48MT/ES-A|24|24|Transistor (sink)|62500"
            ],
            Title = "{Brand} {Series} {Model} PLC Base Unit, {Inputs} In / {Outputs} Out ({Output type})",
            Intro = "The {Model} is a base unit of the MELSEC FX3U family: 64 k-step program memory, 0.065 µs per basic instruction and up to 384 I/O points when expanded. It remains one of the most widely installed controllers on Bangladeshi packaging, textile and sewing-line machines, which makes spares and programming support easy to find.",
            Highlights = ["{Inputs} inputs / {Outputs} outputs, {Output type} output", "64 k steps program memory, 0.065 µs basic instruction", "100–240 V AC supply with built-in 24 V DC service supply", "RS-422 programming port, expandable with FX3U-ENET, -485ADP and analog adapters"],
            Applications = ["Garment and textile machinery", "Pouch and form-fill-seal machines", "Boiler and utility sequencing", "Machine retrofits"],
            Specs = ["General:Model=Model", "Electrical:Inputs=Digital inputs;Outputs=Digital outputs;Output type=Output type"],
            Fixed = ["Electrical:Supply voltage=100–240 V AC, 50/60 Hz", "Memory:Program capacity=64 000 steps (built-in RAM)", "Environment:Ambient temperature=0 to 55 °C", "General:Programming software=GX Works2 / GX Developer"],
            Shape = Shape.DinModule,
            WeightKg = 0.65m,
            BoxCm = (22m, 12m, 9m),
            Popularity = 1.4
        };

        yield return new Family
        {
            Leaf = "micro-nano-plcs",
            Brand = "Delta Electronics",
            Series = "DVP-S Series",
            NounEn = "PLC Main Unit",
            NounBn = "পিএলসি মেইন ইউনিট",
            Header = "Model|Inputs|Outputs|Output type|Supply|Price",
            Rows =
            [
                "DVP14SS211R|8|6|Relay|24 V DC|9800",
                "DVP14SS211T|8|6|Transistor (NPN)|24 V DC|9400",
                "DVP12SA211R|8|4|Relay|24 V DC|11500",
                "DVP12SA211T|8|4|Transistor (NPN)|24 V DC|11200",
                "DVP20EX200R|8|6|Relay|24 V DC|19800",
                "DVP20EX200T|8|6|Transistor (NPN)|24 V DC|19200"
            ],
            Title = "{Brand} {Model} PLC Main Unit, {Inputs} DI / {Outputs} DO, {Output type}",
            Intro = "{Model} is a cost-effective Delta PLC main unit with {Inputs} inputs and {Outputs} outputs, RS-232 and RS-485 ports and Modbus support. It is a popular choice for single-machine control and for pairing with Delta DOP-100 HMIs and VFD-EL drives over a single RS-485 bus.",
            Highlights = ["{Inputs} DI / {Outputs} DO, {Output type} output", "Built-in RS-232 and RS-485 with Modbus ASCII/RTU", "{Supply} supply, compact DIN-rail or screw mounting", "Programmed with ISPSoft / WPLSoft"],
            Applications = ["Single-machine automation", "Pump and tank-level control", "Small conveyor systems"],
            Specs = ["General:Model=Model", "Electrical:Inputs=Digital inputs;Outputs=Digital outputs;Output type=Output type;Supply=Supply voltage"],
            Fixed = ["Communication:Ports=RS-232 + RS-485 (Modbus)", "Environment:Ambient temperature=0 to 55 °C", "General:Programming software=ISPSoft / WPLSoft"],
            Shape = Shape.DinModule,
            WeightKg = 0.3m,
            BoxCm = (14m, 10m, 8m),
            Popularity = 1.5
        };

        yield return new Family
        {
            Leaf = "digital-io-modules",
            Brand = "Siemens",
            Series = "SIMATIC S7-1200 Signal Modules",
            NounEn = "Digital Signal Module",
            NounBn = "ডিজিটাল সিগন্যাল মডিউল",
            Header = "Model|MPN|Channels|Type|Price",
            Rows =
            [
                "SM 1221 DI 8 × 24 V DC|6ES7221-1BF32-0XB0|8 DI|Digital input|13800",
                "SM 1221 DI 16 × 24 V DC|6ES7221-1BH32-0XB0|16 DI|Digital input|20500",
                "SM 1222 DQ 8 × 24 V DC|6ES7222-1BF32-0XB0|8 DO|Digital output (transistor)|16200",
                "SM 1222 DQ 16 × 24 V DC|6ES7222-1BH32-0XB0|16 DO|Digital output (transistor)|24800",
                "SM 1222 DQ 8 × Relay|6ES7222-1HF32-0XB0|8 DO|Digital output (relay)|17800",
                "SM 1222 DQ 16 × Relay|6ES7222-1HH32-0XB0|16 DO|Digital output (relay)|27500",
                "SM 1223 DI 8 / DQ 8 × 24 V DC|6ES7223-1BH32-0XB0|8 DI + 8 DO|Digital combination|21500",
                "SM 1223 DI 16 / DQ 16 × 24 V DC|6ES7223-1BL32-0XB0|16 DI + 16 DO|Digital combination|34500"
            ],
            Title = "{Brand} {Model} Digital Signal Module ({MPN})",
            Intro = "The {Model} extends an S7-1200 CPU with {Channels}. Signal modules snap onto the right side of the CPU on the same DIN rail and are configured automatically in TIA Portal; up to eight modules can be fitted to a CPU 1214C or larger.",
            Highlights = ["{Channels} — {Type}", "Connects directly to the right of an S7-1200 CPU", "Hot-pluggable terminal blocks for fast commissioning", "Diagnostics per channel in TIA Portal"],
            Applications = ["Adding I/O to S7-1200 machine controllers", "Valve manifold and relay interfacing", "Panel retrofits with limited space"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model", "Electrical:Channels=Channels;Type=Signal type"],
            Fixed = ["Electrical:Rated voltage=24 V DC", "Environment:Degree of protection=IP20", "Environment:Ambient temperature=-20 to 60 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.2m,
            BoxCm = (12m, 8m, 7m),
            Popularity = 0.9
        };

        yield return new Family
        {
            Leaf = "analog-io-modules",
            Brand = "Siemens",
            Series = "SIMATIC S7-1200 Analog Modules",
            NounEn = "Analog Signal Module",
            NounBn = "অ্যানালগ সিগন্যাল মডিউল",
            Header = "Model|MPN|Channels|Signal|Resolution|Price",
            Rows =
            [
                "SM 1231 AI 4 × 13 bit|6ES7231-4HD32-0XB0|4 AI|±10 V, ±5 V, 0–20 mA, 4–20 mA|13 bit|26500",
                "SM 1231 AI 8 × 13 bit|6ES7231-4HF32-0XB0|8 AI|±10 V, ±5 V, 0–20 mA, 4–20 mA|13 bit|39500",
                "SM 1232 AQ 2 × 14 bit|6ES7232-4HB32-0XB0|2 AO|±10 V, 0–20 mA, 4–20 mA|14 bit|27800",
                "SM 1232 AQ 4 × 14 bit|6ES7232-4HD32-0XB0|4 AO|±10 V, 0–20 mA, 4–20 mA|14 bit|38500",
                "SM 1234 AI 4 / AQ 2|6ES7234-4HE32-0XB0|4 AI + 2 AO|±10 V, 0–20 mA, 4–20 mA|13 / 14 bit|34500",
                "SM 1231 AI 4 × RTD|6ES7231-5PD32-0XB0|4 RTD|Pt100 / Pt1000 / Ni100|16 bit|41500",
                "SM 1231 AI 4 × TC|6ES7231-5QD32-0XB0|4 TC|Thermocouple J, K, T, E, R, S, N|16 bit|43500"
            ],
            Title = "{Brand} {Model} Analog Module ({MPN})",
            Intro = "The {Model} brings {Channels} into an S7-1200 system. It is the usual choice for pressure, level, flow and temperature loops in ETP, boiler and HVAC applications where 4–20 mA field instruments are wired to the PLC.",
            Highlights = ["{Channels}: {Signal}", "{Resolution} resolution", "Wire-break and over/under-range diagnostics", "Clips onto an S7-1200 CPU, no separate power wiring needed"],
            Applications = ["4–20 mA pressure and level transmitters", "Temperature loops with RTD/thermocouples", "Analog speed references for drives"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model", "Electrical:Channels=Channels;Signal=Signal range;Resolution=Resolution"],
            Fixed = ["Electrical:Rated voltage=24 V DC", "Environment:Ambient temperature=-20 to 60 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.2m,
            BoxCm = (12m, 8m, 7m),
            Popularity = 0.8
        };

        yield return new Family
        {
            Leaf = "plc-communication-modules",
            Brand = "Siemens",
            Series = "SIMATIC S7-1200 Communication",
            NounEn = "Communication Module",
            NounBn = "কমিউনিকেশন মডিউল",
            Header = "Model|MPN|Interface|Price",
            Rows =
            [
                "CM 1241 RS485|6ES7241-1CH32-0XB0|RS-485 (Modbus RTU, USS, freeport)|16800",
                "CM 1241 RS232|6ES7241-1AH32-0XB0|RS-232 (Modbus RTU, freeport)|15800",
                "CB 1241 RS485 Communication Board|6ES7241-1CH30-1XB0|RS-485 on-CPU board|11800",
                "CP 1243-1 Ethernet Processor|6GK7243-1BX30-0XE0|Ethernet with VPN/firewall for telecontrol|58500",
                "CM 1243-5 PROFIBUS DP Master|6GK7243-5DX30-0XE0|PROFIBUS DP master|52500",
                "CSM 1277 4-port Switch|6GK7277-1AA10-0AA0|4-port Industrial Ethernet switch|14500"
            ],
            Title = "{Brand} {Model} for S7-1200 ({MPN})",
            Intro = "The {Model} adds {Interface} to an S7-1200 controller, letting it talk to drives, meters, SCADA systems and field devices without a separate gateway.",
            Highlights = ["Interface: {Interface}", "Plugs into the S7-1200 expansion bus", "Configured in TIA Portal"],
            Applications = ["Modbus RTU links to energy meters and drives", "PROFIBUS integration with legacy devices", "Ethernet fan-out for SCADA and HMI"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model", "Communication:Interface=Interface"],
            Fixed = ["Environment:Ambient temperature=-20 to 60 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.16m,
            BoxCm = (12m, 8m, 7m),
            Popularity = 0.7
        };

        yield return new Family
        {
            Leaf = "plc-power-supplies",
            Brand = "Siemens",
            Series = "SITOP",
            NounEn = "DIN-rail Power Supply",
            NounBn = "ডিআইএন-রেল পাওয়ার সাপ্লাই",
            Header = "Model|MPN|Output|Input|Price",
            Rows =
            [
                "PM1207 24 V / 2.5 A|6EP1332-1SH71|24 V DC / 2.5 A|120/230 V AC|16500",
                "PSU100S 24 V / 2.5 A|6EP1332-2BA20|24 V DC / 2.5 A|120/230 V AC|19800",
                "PSU100S 24 V / 5 A|6EP1333-2BA20|24 V DC / 5 A|120/230 V AC|26500",
                "PSU100S 24 V / 10 A|6EP1334-2BA20|24 V DC / 10 A|120/230 V AC|42500"
            ],
            Title = "{Brand} {Series} {Model} Power Supply ({MPN})",
            Intro = "The {Series} {Model} is a regulated single-phase power supply delivering {Output} from a {Input} input. It matches the S7-1200 colour scheme and mounts on the same DIN rail.",
            Highlights = ["Output {Output}, input {Input}", "Wide-range input with power-factor correction", "Output voltage LED and overload protection", "Efficiency above 89 %"],
            Applications = ["Supplying S7-1200 CPUs and signal modules", "Powering 24 V sensors and valves"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model", "Electrical:Output=Output;Input=Input"],
            Fixed = ["Environment:Degree of protection=IP20", "Environment:Ambient temperature=-25 to 70 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.5m,
            BoxCm = (13m, 10m, 8m),
            Popularity = 1.0
        };

        yield return new Family
        {
            Leaf = "plc-power-supplies",
            Brand = "Delta Electronics",
            Series = "DRP Series",
            NounEn = "DIN-rail Power Supply",
            NounBn = "ডিআইএন-রেল পাওয়ার সাপ্লাই",
            Header = "Model|Output|Power|Price",
            Rows =
            [
                "DRP024V060W1AZ|24 V DC / 2.5 A|60 W|6200",
                "DRP024V120W1AZ|24 V DC / 5 A|120 W|9800",
                "DRP024V240W1AZ|24 V DC / 10 A|240 W|15800",
                "DRP024V480W1AZ|24 V DC / 20 A|480 W|27500"
            ],
            Title = "{Brand} {Model} DIN-rail Power Supply {Output} ({Power})",
            Intro = "The Delta {Model} is a {Power} single-phase DIN-rail supply with {Output} output, short-circuit, overload and over-voltage protection. A cost-effective way to power PLCs, HMIs and sensor networks inside control panels.",
            Highlights = ["{Output} / {Power} DIN-rail supply", "Universal 85–264 V AC input", "Efficiency up to 91 %", "Metal case, -25 to 70 °C operating range"],
            Applications = ["Control-panel 24 V distribution", "PLC and HMI supply", "Sensor and valve-island power"],
            Specs = ["General:Model=Model", "Electrical:Output=Output;Power=Rated power"],
            Fixed = ["Electrical:Input voltage=85–264 V AC", "Environment:Ambient temperature=-25 to 70 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.45m,
            BoxCm = (13m, 10m, 8m),
            Popularity = 1.3
        };

        // ───────────────────────── 2. HMI & Industrial PCs ─────────────────────────
        yield return new Family
        {
            Leaf = "compact-hmi-panels",
            Brand = "Delta Electronics",
            Series = "DOP-100",
            NounEn = "HMI Touch Panel",
            NounBn = "এইচএমআই টাচ প্যানেল",
            Header = "Model|Size|Resolution|Ports|Price",
            Rows =
            [
                "DOP-103BQ|4.3″|480 × 272|1 × COM (RS-232/422/485), USB host|14500",
                "DOP-107BV|7″|800 × 480|1 × COM, USB host/client|21800",
                "DOP-107EV|7″|800 × 480|Ethernet, 2 × COM, USB|26500",
                "DOP-110CS|10.1″|1024 × 600|Ethernet, 3 × COM, USB|42500",
                "DOP-107WV|7″ widescreen|800 × 480|COM, USB, SD card|23800"
            ],
            Title = "{Brand} {Model} {Size} HMI Touch Panel ({Resolution})",
            Intro = "The Delta {Model} is a {Size} colour TFT HMI with {Resolution} resolution and a resistive touch screen, programmed in DOPSoft. It pairs naturally with Delta DVP PLCs and VFD drives over RS-485, but supports Modbus, Siemens, Mitsubishi and Omron protocols as well.",
            Highlights = ["{Size} TFT, {Resolution}", "Interfaces: {Ports}", "Drivers for Delta, Siemens, Mitsubishi, Omron, Allen-Bradley and Modbus", "Recipe, alarm, trend and data-logging functions"],
            Applications = ["Machine operator interfaces", "ETP and pump-house control panels", "Plant-floor status display"],
            Specs = ["General:Model=Model", "Display:Size=Screen size;Resolution=Resolution", "Communication:Ports=Interfaces"],
            Fixed = ["Display:Touch=Resistive, 4-wire", "Electrical:Supply voltage=24 V DC (±10 %)", "Environment:Degree of protection=IP65 (front panel)", "General:Software=DOPSoft"],
            Shape = Shape.Panel,
            WeightKg = 0.6m,
            BoxCm = (24m, 18m, 8m),
            Popularity = 1.5
        };

        yield return new Family
        {
            Leaf = "compact-hmi-panels",
            Brand = "Siemens",
            Series = "SIMATIC HMI Basic Panels",
            NounEn = "Basic HMI Panel",
            NounBn = "বেসিক এইচএমআই প্যানেল",
            Header = "Model|MPN|Size|Resolution|Price",
            Rows =
            [
                "KTP400 Basic PN|6AV2123-2DB03-0AX0|4.3″|480 × 272|39500",
                "KTP700 Basic PN|6AV2123-2GB03-0AX0|7″|800 × 480|59500",
                "KTP900 Basic PN|6AV2123-2JB03-0AX0|9″|800 × 480|82500"
            ],
            Title = "{Brand} {Model} {Size} Basic HMI Panel ({MPN})",
            Intro = "The {Model} is the entry panel in the SIMATIC HMI line: a {Size} touch display with PROFINET, configured in WinCC (TIA Portal) alongside the S7-1200 or S7-1500 project. Ideal for machine-level operation with keys, alarms and trends.",
            Highlights = ["{Size} touch display, {Resolution}", "PROFINET and USB interfaces", "Configured in WinCC (TIA Portal)", "Supports alarms, recipes and trends"],
            Applications = ["Operator panels for S7-1200/1500 machines", "Utility monitoring"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model", "Display:Size=Screen size;Resolution=Resolution"],
            Fixed = ["Communication:Interface=PROFINET, USB", "Electrical:Supply voltage=24 V DC", "Environment:Degree of protection=IP65 (front)"],
            Shape = Shape.Panel,
            WeightKg = 0.5m,
            BoxCm = (26m, 19m, 8m),
            Popularity = 1.0
        };

        yield return new Family
        {
            Leaf = "standard-hmi-panels",
            Brand = "Siemens",
            Series = "SIMATIC HMI Comfort Panels",
            NounEn = "Comfort HMI Panel",
            NounBn = "কমফোর্ট এইচএমআই প্যানেল",
            Header = "Model|MPN|Size|Resolution|Price",
            Rows =
            [
                "TP700 Comfort|6AV2124-0GC01-0AX0|7″ widescreen|800 × 480|165000",
                "TP900 Comfort|6AV2124-0JC01-0AX0|9″ widescreen|800 × 480|198000",
                "TP1200 Comfort|6AV2124-0MC01-0AX0|12″ widescreen|1280 × 800|265000"
            ],
            Title = "{Brand} {Model} {Size} Comfort HMI Panel ({MPN})",
            Intro = "The {Model} is a high-end SIMATIC Comfort Panel with a {Size} multi-touch display, PROFINET, PROFIBUS-capable MPI/DP and an SD slot. It supports scripting, archiving and web access, making it suited to production lines and process plants.",
            Highlights = ["{Size} multi-touch display, {Resolution}", "PROFINET, PROFIBUS/MPI, 2 × USB, SD card slot", "Archives, scripts, web server and audit trail options", "Aluminium front, IP65"],
            Applications = ["Production-line supervision", "Process plants and ETP", "Recipe-driven batch machines"],
            Specs = ["General:MPN=Manufacturer part no.;Model=Model", "Display:Size=Screen size;Resolution=Resolution"],
            Fixed = ["Communication:Interface=PROFINET, PROFIBUS/MPI, USB", "Electrical:Supply voltage=24 V DC", "Environment:Degree of protection=IP65 (front)"],
            Shape = Shape.Panel,
            WeightKg = 1.9m,
            BoxCm = (36m, 28m, 12m),
            Popularity = 0.7,
            WarrantyMonths = 24
        };

        yield return new Family
        {
            Leaf = "standard-hmi-panels",
            Brand = "Weintek",
            Series = "MT8000iE",
            NounEn = "HMI Touch Panel",
            NounBn = "এইচএমআই টাচ প্যানেল",
            Header = "Model|Size|Resolution|CPU|Price",
            Rows =
            [
                "MT8071iE|7″|800 × 480|Cortex-A8 600 MHz|27500",
                "MT8102iE|10.1″|1024 × 600|Cortex-A8 600 MHz|41500",
                "MT8121XE|12.1″|1024 × 768|Cortex-A8 600 MHz|68500",
                "MT8150XE|15″|1024 × 768|Cortex-A8 600 MHz|82500"
            ],
            Title = "{Brand} {Model} {Size} HMI with Ethernet ({Resolution})",
            Intro = "The Weintek {Model} is a {Size} HMI with Ethernet, USB host/client and RS-232/485 ports, programmed in EasyBuilder Pro. A broad driver library covers almost every PLC brand found in Bangladeshi factories, which makes it a flexible replacement HMI.",
            Highlights = ["{Size} TFT, {Resolution}", "{CPU}, 256 MB flash", "Ethernet + 2 COM + USB host/client", "300+ PLC and device drivers"],
            Applications = ["Replacing obsolete HMIs", "Multi-brand plants with mixed PLCs", "Remote monitoring via EasyAccess"],
            Specs = ["General:Model=Model", "Display:Size=Screen size;Resolution=Resolution", "General:CPU=Processor"],
            Fixed = ["Communication:Interfaces=Ethernet, COM1/COM3 RS-232/485, USB host & client", "Electrical:Supply voltage=24 V DC", "Environment:Degree of protection=IP65 (front)"],
            Shape = Shape.Panel,
            WeightKg = 0.9m,
            BoxCm = (30m, 22m, 10m),
            Popularity = 0.9
        };

        yield return new Family
        {
            Leaf = "panel-pcs",
            Brand = "Advantech",
            Series = "PPC-3000",
            NounEn = "Fanless Panel PC",
            NounBn = "ফ্যানলেস প্যানেল পিসি",
            Header = "Model|Size|Resolution|Memory|Price",
            Rows =
            [
                "PPC-3100S|10.1″|1280 × 800|4 GB DDR3L|118000",
                "PPC-3120|12.1″|1024 × 768|4 GB DDR3L|132000",
                "PPC-3150|15″|1024 × 768|4 GB DDR3L|148000",
                "PPC-3170|17″|1280 × 1024|4 GB DDR3L|165000",
                "PPC-3190|19″|1280 × 1024|8 GB DDR3L|182000",
                "PPC-3211W|21.5″ widescreen|1920 × 1080|8 GB DDR3L|205000"
            ],
            Title = "{Brand} {Series} {Model} {Size} Fanless Industrial Panel PC",
            Intro = "The {Model} is a fanless industrial panel PC with a {Size} projected-capacitive touch display, {Memory}, SSD storage and Windows 10 IoT support. A single device can host SCADA clients, historians and local HMI without a cabinet-mounted computer.",
            Highlights = ["{Size} touch display, {Resolution}", "{Memory}, fanless Intel processor", "Dual Ethernet, USB 3.0 and RS-232/422/485", "Wide 9–36 V DC input"],
            Applications = ["SCADA client stations", "Machine HMI with data logging", "Edge visualisation for ETP and utilities"],
            Specs = ["General:Model=Model", "Display:Size=Screen size;Resolution=Resolution", "Memory:Memory=System memory"],
            Fixed = ["Electrical:Power input=9–36 V DC", "Environment:Degree of protection=IP66 (front)", "Environment:Operating temperature=-20 to 60 °C", "General:Operating system=Windows 10 IoT Enterprise (optional)"],
            Shape = Shape.Panel,
            WeightKg = 3.2m,
            BoxCm = (46m, 36m, 14m),
            Popularity = 0.5,
            WarrantyMonths = 24
        };

        yield return new Family
        {
            Leaf = "iiot-gateways",
            Brand = "Moxa",
            Series = "MGate",
            NounEn = "Protocol Gateway",
            NounBn = "প্রোটোকল গেটওয়ে",
            Header = "Model|Function|Ports|Price",
            Rows =
            [
                "MGate MB3170|Modbus TCP ⇄ Modbus RTU/ASCII|1 × RS-232/422/485|34500",
                "MGate MB3270|Modbus TCP ⇄ Modbus RTU/ASCII|2 × RS-232/422/485|42500",
                "MGate MB3480|Modbus TCP ⇄ Modbus RTU/ASCII|4 × RS-232/422/485|58500",
                "MGate 5105-MB-EIP|Modbus RTU/TCP ⇄ EtherNet/IP|1 × RS-232/422/485|61500",
                "MGate 5101-PBM-MN|PROFIBUS master ⇄ Modbus TCP|1 × PROFIBUS DB9|72500"
            ],
            Title = "{Brand} {Model} Industrial Protocol Gateway ({Function})",
            Intro = "The {Model} converts {Function} so older serial instruments, meters and drives can join an Ethernet-based SCADA or PLC network. Configuration is browser-based; no PLC programming is required.",
            Highlights = ["Protocol conversion: {Function}", "Ports: {Ports}", "Browser-based configuration, 2 kV isolation on serial ports", "-40 to 75 °C versions available on request"],
            Applications = ["Connecting energy meters and VFDs to SCADA", "Legacy PLC integration", "ETP remote monitoring"],
            Specs = ["General:Model=Model", "Communication:Function=Protocol conversion;Ports=Serial ports"],
            Fixed = ["Electrical:Supply voltage=12–48 V DC", "Environment:Operating temperature=-10 to 60 °C"],
            Shape = Shape.DinModule,
            WeightKg = 0.34m,
            BoxCm = (16m, 12m, 7m),
            Popularity = 0.7
        };

        yield return new Family
        {
            Leaf = "pushbuttons-selector-switches",
            Brand = "Schneider Electric",
            Series = "Harmony XB4",
            NounEn = "22 mm Control Station Unit",
            NounBn = "২২ মিমি কন্ট্রোল স্টেশন ইউনিট",
            Header = "Model|Function|Contacts|Price",
            Rows =
            [
                "XB4BA21|Black flush pushbutton|1 NO|520",
                "XB4BA31|Green flush pushbutton|1 NO|520",
                "XB4BA42|Red flush pushbutton|1 NC|520",
                "XB4BS542|Red mushroom Ø40 emergency stop, turn to release|1 NC|1350",
                "XB4BD21|2-position stay-put selector, black|1 NO|690",
                "XB4BD25|3-position stay-put selector, black|2 NO|850"
            ],
            Title = "{Brand} Harmony {Model} 22 mm {Function} ({Contacts})",
            Intro = "The Harmony {Model} is a 22 mm metal-bezel operator: {Function}, supplied with {Contacts}. The XB4 range is the standard operator line on many Bangladeshi control panels and is stocked in all common colours.",
            Highlights = ["{Function}", "Contact block: {Contacts}", "Chromium-plated metal bezel, IP66 front", "Mounts in a standard Ø22.5 mm hole"],
            Applications = ["Start/stop stations", "Machine operator panels", "Emergency stop circuits"],
            Specs = ["General:Model=Model;Function=Function", "Electrical:Contacts=Contact configuration"],
            Fixed = ["Environment:Degree of protection=IP66 (front)", "General:Mounting hole=Ø22.5 mm", "Electrical:Rated voltage=240 V AC / 24 V DC"],
            Shape = Shape.Pushbutton,
            WeightKg = 0.09m,
            BoxCm = (7m, 7m, 8m),
            Popularity = 1.6,
            WarrantyMonths = 18
        };

        yield return new Family
        {
            Leaf = "indicator-lights-beacons",
            Brand = "Schneider Electric",
            Series = "Harmony XB4 Pilot Lights",
            NounEn = "22 mm LED Pilot Light",
            NounBn = "২২ মিমি এলইডি পাইলট লাইট",
            Header = "Model|Colour|Price",
            Rows =
            [
                "XB4BVB1|White|820",
                "XB4BVB3|Green|820",
                "XB4BVB4|Red|820",
                "XB4BVB5|Yellow|820",
                "XB4BVB6|Blue|820"
            ],
            Title = "{Brand} {Model} 22 mm {Colour} LED Pilot Light, 24 V AC/DC",
            Intro = "The {Model} is a 22 mm integral-LED pilot light in {Colour} with 24 V AC/DC supply. Long-life LEDs and a metal bezel make it a reliable status indicator on machine and panel doors.",
            Highlights = ["{Colour} integral LED, 24 V AC/DC", "Metal bezel, IP66 front", "Screw-clamp terminals", "Mounts in a Ø22.5 mm hole"],
            Applications = ["Run / stop / fault indication", "Panel door status lights"],
            Specs = ["General:Model=Model;Colour=Colour"],
            Fixed = ["Electrical:Supply voltage=24 V AC/DC", "Environment:Degree of protection=IP66 (front)"],
            Shape = Shape.Pushbutton,
            WeightKg = 0.05m,
            BoxCm = (7m, 7m, 7m),
            Popularity = 1.2,
            WarrantyMonths = 18
        };

        yield return new Family
        {
            Leaf = "indicator-lights-beacons",
            Brand = "Eaton",
            Series = "RMQ-Titan M22",
            NounEn = "22 mm LED Indicator",
            NounBn = "২২ মিমি এলইডি ইন্ডিকেটর",
            Header = "Model|Colour|Price",
            Rows =
            [
                "M22-L-W|White|780",
                "M22-L-G|Green|780",
                "M22-L-R|Red|780",
                "M22-L-Y|Yellow|780",
                "M22-L-B|Blue|780"
            ],
            Title = "{Brand} {Series} {Model} {Colour} Indicator Light Head (22 mm)",
            Intro = "The Eaton {Model} is a {Colour} indicator light head from the RMQ-Titan M22 system. Fit the M22-LED element for 12–30 V or 85–264 V operation; the flat or raised lens options suit different panel layouts.",
            Highlights = ["{Colour} lens, 22.5 mm mounting", "Compatible with M22-LED and M22-LEDC-230 elements", "Plastic or metal bezel options", "IP66 / IP67 / IP69K front sealing"],
            Applications = ["Machine status indication", "Control panel doors"],
            Specs = ["General:Model=Model;Colour=Colour"],
            Fixed = ["Environment:Degree of protection=IP66 / IP67 / IP69K"],
            Shape = Shape.Pushbutton,
            WeightKg = 0.04m,
            BoxCm = (6m, 6m, 6m),
            Popularity = 0.8
        };

        // ───────────────────────── 3. Drives & Motion Control ─────────────────────────
        yield return new Family
        {
            Leaf = "compact-ac-drives",
            Brand = "Delta Electronics",
            Series = "VFD-EL",
            NounEn = "AC Drive",
            NounBn = "এসি ড্রাইভ",
            Header = "Model|Power|Supply|Current|Price",
            Rows =
            [
                "VFD002EL21A|0.25 kW (⅓ HP)|1-phase 200–240 V|1.6 A|9800",
                "VFD004EL21A|0.4 kW (½ HP)|1-phase 200–240 V|2.5 A|10800",
                "VFD007EL21A|0.75 kW (1 HP)|1-phase 200–240 V|4.2 A|12500",
                "VFD015EL21A|1.5 kW (2 HP)|1-phase 200–240 V|7.5 A|15500",
                "VFD022EL21A|2.2 kW (3 HP)|1-phase 200–240 V|11 A|18500",
                "VFD004EL43A|0.4 kW (½ HP)|3-phase 380–480 V|1.5 A|11800",
                "VFD007EL43A|0.75 kW (1 HP)|3-phase 380–480 V|2.7 A|14500",
                "VFD015EL43A|1.5 kW (2 HP)|3-phase 380–480 V|4.2 A|17500",
                "VFD022EL43A|2.2 kW (3 HP)|3-phase 380–480 V|5.5 A|20500",
                "VFD037EL43A|3.7 kW (5 HP)|3-phase 380–480 V|8.5 A|26500"
            ],
            Title = "{Brand} {Series} {Model} Compact AC Drive {Power}, {Supply}",
            Intro = "The Delta {Model} is a compact general-purpose AC drive rated {Power} on a {Supply} supply. It offers sensorless vector control, built-in RS-485 Modbus, an integrated PLC function and a removable keypad, making it a common choice for conveyors, fans, pumps and packaging machines.",
            Highlights = ["{Power}, {Supply}, rated output {Current}", "Built-in RS-485 Modbus RTU and 16-step PLC function", "Auto-torque boost and slip compensation", "Compact book-size footprint for crowded panels"],
            Applications = ["Conveyors and packaging lines", "Fans and blowers", "Small pumps and compressors", "Textile machine retrofits"],
            Specs = ["General:Model=Model", "Electrical:Power=Motor power;Supply=Supply;Current=Rated output current"],
            Fixed = ["Electrical:Output frequency=0.1–600 Hz", "Control:Control mode=V/F, sensorless vector", "Communication:Interface=RS-485 Modbus RTU", "Environment:Degree of protection=IP20", "Environment:Ambient temperature=-10 to 50 °C"],
            Shape = Shape.DinModule,
            WeightKg = 1.1m,
            BoxCm = (22m, 14m, 14m),
            Popularity = 1.8,
            FilterSpecs = ["Motor power", "Supply"]
        };

        yield return new Family
        {
            Leaf = "compact-ac-drives",
            Brand = "Schneider Electric",
            Series = "Altivar 12",
            NounEn = "AC Drive",
            NounBn = "এসি ড্রাইভ",
            Header = "Model|Power|Supply|Current|Price",
            Rows =
            [
                "ATV12H018M2|0.18 kW (¼ HP)|1-phase 200–240 V|1.4 A|11500",
                "ATV12H037M2|0.37 kW (½ HP)|1-phase 200–240 V|2.4 A|13500",
                "ATV12H075M2|0.75 kW (1 HP)|1-phase 200–240 V|4.2 A|16800",
                "ATV12HU15M2|1.5 kW (2 HP)|1-phase 200–240 V|7.5 A|21500",
                "ATV12HU22M2|2.2 kW (3 HP)|1-phase 200–240 V|10 A|26500"
            ],
            Title = "{Brand} {Series} {Model} AC Drive {Power}, {Supply}",
            Intro = "The Altivar 12 {Model} is a compact drive for simple machines, rated {Power} on a {Supply} supply. It ships with a built-in keypad and potentiometer, Modbus and CANopen options and a configuration kit for quick commissioning.",
            Highlights = ["{Power}, {Supply}, {Current}", "Integrated Modbus, optional CANopen", "Built-in EMC filter", "Keypad with potentiometer for quick set-up"],
            Applications = ["Conveyors", "Pumps and fans", "Simple packaging machines"],
            Specs = ["General:Model=Model", "Electrical:Power=Motor power;Supply=Supply;Current=Rated output current"],
            Fixed = ["Control:Control mode=Standard V/F, sensorless flux vector", "Environment:Degree of protection=IP20", "Environment:Ambient temperature=-10 to 50 °C"],
            Shape = Shape.DinModule,
            WeightKg = 1.0m,
            BoxCm = (22m, 14m, 14m),
            Popularity = 1.0,
            FilterSpecs = ["Motor power", "Supply"]
        };

        yield return new Family
        {
            Leaf = "general-purpose-ac-drives",
            Brand = "ABB",
            Series = "ACS580-01",
            NounEn = "General Purpose AC Drive",
            NounBn = "জেনারেল পারপাস এসি ড্রাইভ",
            Header = "Model|MPN|Power|Current|Price",
            Rows =
            [
                "ACS580-01-02A7-4|ACS580-01-02A7-4|0.75 kW|2.6 A|42500",
                "ACS580-01-04A1-4|ACS580-01-04A1-4|1.5 kW|4.0 A|52500",
                "ACS580-01-05A7-4|ACS580-01-05A7-4|2.2 kW|5.6 A|58500",
                "ACS580-01-12A7-4|ACS580-01-12A7-4|5.5 kW|12.5 A|84500",
                "ACS580-01-025A-4|ACS580-01-025A-4|11 kW|25 A|128000",
                "ACS580-01-032A-4|ACS580-01-032A-4|15 kW|32 A|152000",
                "ACS580-01-045A-4|ACS580-01-045A-4|22 kW|44 A|198000",
                "ACS580-01-072A-4|ACS580-01-072A-4|37 kW|72 A|285000"
            ],
            Title = "{Brand} {Model} General Purpose Drive, {Power}, 380–480 V",
            Intro = "The {Model} belongs to the ABB ACS580 general-purpose drive family: {Power} on a three-phase 380–480 V supply with a common control panel, built-in EMC filter, PID control and a Bluetooth-capable assistant panel. A good fit for pumps, fans, compressors and conveyors from 0.75 kW up to 250 kW.",
            Highlights = ["{Power}, 3-phase 380–480 V, {Current} rated output", "Assistant control panel with built-in start-up assistants", "Built-in Modbus RTU, optional PROFINET / EtherNet/IP / PROFIBUS", "Swinging-choke design lowers harmonics, no external line reactor needed", "IP21 as standard"],
            Applications = ["Water and ETP pumps", "HVAC fans and chillers", "Compressors", "Material handling"],
            Specs = ["General:MPN=Type designation;Model=Model", "Electrical:Power=Motor power;Current=Rated output current"],
            Fixed = ["Electrical:Supply=3-phase 380–480 V, 50/60 Hz", "Control:Control mode=Scalar, vector (DTC option)", "Communication:Interface=Embedded Modbus RTU; fieldbus options", "Environment:Degree of protection=IP21", "Environment:Ambient temperature=-15 to 50 °C"],
            Shape = Shape.DinModule,
            WeightKg = 4.2m,
            BoxCm = (40m, 22m, 22m),
            Popularity = 1.2,
            WarrantyMonths = 24,
            FilterSpecs = ["Motor power"]
        };

        yield return new Family
        {
            Leaf = "general-purpose-ac-drives",
            Brand = "Yaskawa",
            Series = "V1000",
            NounEn = "Compact Vector Drive",
            NounBn = "কমপ্যাক্ট ভেক্টর ড্রাইভ",
            Header = "Model|Power|Current|Price",
            Rows =
            [
                "CIMR-VC4A0001BAA|0.4 kW|1.2 A|31500",
                "CIMR-VC4A0002BAA|0.75 kW|2.1 A|36500",
                "CIMR-VC4A0004BAA|1.5 kW|4.1 A|44500",
                "CIMR-VC4A0005BAA|2.2 kW|5.4 A|51500",
                "CIMR-VC4A0007BAA|3.7 kW|8.8 A|63500"
            ],
            Title = "{Brand} {Series} {Model} Compact Vector AC Drive {Power}, 400 V",
            Intro = "The Yaskawa V1000 {Model} is a compact vector-control drive for {Power} motors on 400 V supplies, with high starting torque at low speed and a long maintenance-free life. Popular on textile, spinning and machine-tool applications.",
            Highlights = ["{Power}, 3-phase 380–480 V, {Current}", "Open-loop vector control with 200 % starting torque", "Built-in RS-422/485 Modbus", "Removable terminal block for fast replacement"],
            Applications = ["Spinning and textile machinery", "Machine tools", "Conveyors"],
            Specs = ["General:Model=Model", "Electrical:Power=Motor power;Current=Rated output current"],
            Fixed = ["Control:Control mode=V/f, open-loop vector", "Environment:Degree of protection=IP20", "Environment:Ambient temperature=-10 to 50 °C"],
            Shape = Shape.DinModule,
            WeightKg = 1.5m,
            BoxCm = (24m, 16m, 16m),
            Popularity = 0.8,
            FilterSpecs = ["Motor power"]
        };

        yield return new Family
        {
            Leaf = "servo-drives",
            Brand = "Delta Electronics",
            Series = "ASDA-B3",
            NounEn = "Servo Drive",
            NounBn = "সার্ভো ড্রাইভ",
            Header = "Model|Power|Supply|Price",
            Rows =
            [
                "ASD-B3-0121-F|100 W|1-phase 220 V|31500",
                "ASD-B3-0221-F|200 W|1-phase 220 V|34500",
                "ASD-B3-0421-F|400 W|1-phase 220 V|39500",
                "ASD-B3-0721-F|750 W|1-phase 220 V|46500",
                "ASD-B3-1021-F|1 kW|1-phase 220 V|58500",
                "ASD-B3-1521-F|1.5 kW|1-phase 220 V|66500"
            ],
            Title = "{Brand} {Series} {Model} Servo Drive {Power}, {Supply}",
            Intro = "The ASDA-B3 {Model} is a {Power} servo drive with 24-bit encoder feedback, EtherCAT-ready variants in the family and a 3.1 kHz speed-loop bandwidth. It pairs with the ECM-B3 motor range for pick-and-place, labelling and cutting machines.",
            Highlights = ["{Power} rating, {Supply}", "3.1 kHz speed-loop bandwidth, 24-bit encoder", "Auto-tuning and vibration suppression", "Pulse, analog and internal-position control modes"],
            Applications = ["Cut-to-length and flying shear", "Labelling and packaging", "Pick-and-place and CNC axes"],
            Specs = ["General:Model=Model", "Electrical:Power=Rated power;Supply=Supply"],
            Fixed = ["Control:Control modes=Position, speed, torque", "Communication:Interface=CN3 RS-485 Modbus; USB", "Environment:Degree of protection=IP20"],
            Shape = Shape.DinModule,
            WeightKg = 1.2m,
            BoxCm = (24m, 16m, 14m),
            Popularity = 0.8,
            FilterSpecs = ["Rated power"]
        };

        yield return new Family
        {
            Leaf = "servo-drives",
            Brand = "Mitsubishi Electric",
            Series = "MELSERVO-JE",
            NounEn = "Servo Amplifier",
            NounBn = "সার্ভো অ্যামপ্লিফায়ার",
            Header = "Model|Power|Supply|Price",
            Rows =
            [
                "MR-JE-10A|100 W|1-phase 200–240 V|34500",
                "MR-JE-20A|200 W|1-phase 200–240 V|38500",
                "MR-JE-40A|400 W|1-phase/3-phase 200–240 V|44500",
                "MR-JE-70A|750 W|1-phase/3-phase 200–240 V|56500",
                "MR-JE-100A|1 kW|3-phase 200–240 V|72500",
                "MR-JE-200A|2 kW|3-phase 200–240 V|98500"
            ],
            Title = "{Brand} {Series} {Model} Servo Amplifier {Power}",
            Intro = "The MELSERVO-JE {Model} is a {Power} general-purpose servo amplifier with 4 194 304 pulses/rev encoder resolution and one-touch tuning. It integrates easily with FX3U and iQ-R positioning modules.",
            Highlights = ["{Power}, {Supply}", "4 194 304 p/rev absolute-capable encoder support", "One-touch tuning and advanced vibration suppression", "Pulse train and analog control (A models)"],
            Applications = ["Printing and packaging machines", "Feeding and cutting", "Pick-and-place"],
            Specs = ["General:Model=Model", "Electrical:Power=Rated power;Supply=Supply"],
            Fixed = ["Control:Control modes=Position, speed, torque", "Environment:Degree of protection=IP20"],
            Shape = Shape.DinModule,
            WeightKg = 0.8m,
            BoxCm = (20m, 14m, 12m),
            Popularity = 0.7,
            FilterSpecs = ["Rated power"]
        };

        yield return new Family
        {
            Leaf = "servo-motors",
            Brand = "Mitsubishi Electric",
            Series = "HG-KR",
            NounEn = "Servo Motor",
            NounBn = "সার্ভো মোটর",
            Header = "Model|Power|Torque|Speed|Price",
            Rows =
            [
                "HG-KR13|100 W|0.32 N·m|3000 rpm|28500",
                "HG-KR23|200 W|0.64 N·m|3000 rpm|33500",
                "HG-KR43|400 W|1.3 N·m|3000 rpm|41500",
                "HG-KR73|750 W|2.4 N·m|3000 rpm|53500"
            ],
            Title = "{Brand} {Series} {Model} Servo Motor {Power}, {Torque}",
            Intro = "The {Model} is a low-inertia {Power} servo motor delivering {Torque} at {Speed}. Designed for use with MELSERVO-JE amplifiers, it provides a high-resolution encoder as standard.",
            Highlights = ["{Power}, rated torque {Torque}", "Rated speed {Speed}", "Low-inertia design for rapid positioning", "IP65 (excluding shaft opening)"],
            Applications = ["High-speed positioning axes", "Feeders", "Packaging machines"],
            Specs = ["General:Model=Model", "Mechanical:Power=Rated power;Torque=Rated torque;Speed=Rated speed"],
            Fixed = ["Environment:Degree of protection=IP65", "Mechanical:Encoder=22-bit absolute/incremental"],
            Shape = Shape.Motor,
            WeightKg = 1.1m,
            BoxCm = (22m, 10m, 10m),
            Popularity = 0.6,
            FilterSpecs = ["Rated power"]
        };

        yield return new Family
        {
            Leaf = "servo-motors",
            Brand = "Delta Electronics",
            Series = "ECM-B3",
            NounEn = "Servo Motor",
            NounBn = "সার্ভো মোটর",
            Header = "Model|Power|Torque|Speed|Price",
            Rows =
            [
                "ECM-B3M-C20401RS1|100 W|0.32 N·m|3000 rpm|18500",
                "ECM-B3M-C20602RS1|200 W|0.64 N·m|3000 rpm|22500",
                "ECM-B3M-C20604RS1|400 W|1.27 N·m|3000 rpm|28500",
                "ECM-B3M-C20807RS1|750 W|2.39 N·m|3000 rpm|38500"
            ],
            Title = "{Brand} {Model} Servo Motor {Power} / {Torque}",
            Intro = "The Delta {Model} is a {Power} servo motor with 24-bit incremental encoder, rated {Torque} at {Speed}. It is designed for the ASDA-B3 drive series.",
            Highlights = ["{Power}, {Torque} at {Speed}", "24-bit encoder", "IP65 sealing", "Keyed shaft"],
            Applications = ["Positioning axes", "Packaging", "CNC accessories"],
            Specs = ["General:Model=Model", "Mechanical:Power=Rated power;Torque=Rated torque;Speed=Rated speed"],
            Fixed = ["Environment:Degree of protection=IP65", "Mechanical:Encoder=24-bit incremental"],
            Shape = Shape.Motor,
            WeightKg = 1.0m,
            BoxCm = (22m, 10m, 10m),
            Popularity = 0.8,
            FilterSpecs = ["Rated power"]
        };

        yield return new Family
        {
            Leaf = "soft-starters",
            Brand = "ABB",
            Series = "PSR",
            NounEn = "Soft Starter",
            NounBn = "সফট স্টার্টার",
            Header = "Model|Power|Current|Price",
            Rows =
            [
                "PSR3-600-70|1.5 kW|3 A|22500",
                "PSR6-600-70|3 kW|6 A|25500",
                "PSR9-600-70|4 kW|9 A|28500",
                "PSR12-600-70|5.5 kW|12 A|32500",
                "PSR16-600-70|7.5 kW|16 A|38500",
                "PSR25-600-70|11 kW|25 A|48500"
            ],
            Title = "{Brand} {Model} Soft Starter {Power} at 400 V",
            Intro = "The ABB {Model} is a compact soft starter for {Power} three-phase motors at 400 V. It reduces mechanical shock and inrush current on pumps, fans, compressors and conveyors, with adjustable ramp-up and ramp-down times.",
            Highlights = ["{Power} at 400 V, {Current} rated current", "Adjustable ramp time 1–30 s", "Built-in bypass contacts", "Compact 45 mm-wide housing (PSR3–PSR16)"],
            Applications = ["Pumps and fans", "Compressors", "Conveyors"],
            Specs = ["General:Model=Model", "Electrical:Power=Motor power at 400 V;Current=Rated current"],
            Fixed = ["Electrical:Supply voltage=208–600 V AC", "Environment:Degree of protection=IP20"],
            Shape = Shape.DinModule,
            WeightKg = 0.6m,
            BoxCm = (14m, 10m, 12m),
            Popularity = 0.9,
            FilterSpecs = ["Motor power at 400 V"]
        };

        yield return new Family
        {
            Leaf = "soft-starters",
            Brand = "Schneider Electric",
            Series = "Altistart 22",
            NounEn = "Soft Starter",
            NounBn = "সফট স্টার্টার",
            Header = "Model|Power|Current|Price",
            Rows =
            [
                "ATS22D17Q|7.5 kW|17 A|58500",
                "ATS22D32Q|15 kW|32 A|78500",
                "ATS22D47Q|22 kW|47 A|98500",
                "ATS22D62Q|30 kW|62 A|125000",
                "ATS22D75Q|37 kW|75 A|148000",
                "ATS22D88Q|45 kW|88 A|172000"
            ],
            Title = "{Brand} {Model} Altistart 22 Soft Starter {Power}",
            Intro = "The Altistart 22 {Model} offers torque-control soft start and soft stop for {Power} motors at 230–440 V, with built-in motor protection and a multilingual display. Useful for pumps where pressure surges are a concern.",
            Highlights = ["{Power}, {Current} rated current", "Torque-control soft start / soft stop", "Motor thermal protection built in", "Multilingual 7-segment display"],
            Applications = ["Pumps (water-hammer protection)", "Fans and blowers", "Compressors"],
            Specs = ["General:Model=Model", "Electrical:Power=Motor power;Current=Rated current"],
            Fixed = ["Electrical:Supply voltage=230–440 V AC", "Environment:Degree of protection=IP20"],
            Shape = Shape.DinModule,
            WeightKg = 2.9m,
            BoxCm = (24m, 16m, 16m),
            Popularity = 0.7,
            FilterSpecs = ["Motor power"]
        };

        yield return new Family
        {
            Leaf = "stepper-motors-drivers",
            Brand = "Autonics",
            Series = "A-Series Stepper",
            NounEn = "Stepper Motor",
            NounBn = "স্টেপার মোটর",
            Header = "Model|Frame|Torque|Price",
            Rows =
            [
                "2-phase NEMA 17 stepper motor|42 mm|0.44 N·m|6200",
                "2-phase NEMA 23 stepper motor|57 mm|1.2 N·m|8800",
                "2-phase NEMA 23 stepper motor, long stack|57 mm|2.0 N·m|10500",
                "2-phase NEMA 24 stepper motor|60 mm|3.0 N·m|13500",
                "2-phase NEMA 34 stepper motor|86 mm|4.5 N·m|21500",
                "2-phase NEMA 34 stepper motor, long stack|86 mm|8.5 N·m|28500"
            ],
            Title = "{Brand} {Frame} {Model} {Torque}",
            Intro = "A {Frame} 2-phase hybrid stepper motor with {Torque} holding torque, 1.8° step angle and a 4-lead cable. Suitable for small CNC axes, dosing mechanisms and indexing tables, driven by any micro-step driver of suitable current.",
            Highlights = ["{Frame} frame, {Torque} holding torque", "1.8° step angle, 4-wire connection", "Low vibration at medium speed", "Standard flatted shaft"],
            Applications = ["Small CNC and 3D-printer axes", "Dosing and filling mechanisms", "Indexing tables"],
            Specs = ["General:Model=Description;Frame=Frame size", "Mechanical:Torque=Holding torque"],
            Fixed = ["Mechanical:Step angle=1.8°", "Environment:Insulation class=Class B"],
            Shape = Shape.Motor,
            WeightKg = 0.6m,
            BoxCm = (14m, 10m, 10m),
            Popularity = 0.6
        };

        yield return new Family
        {
            Leaf = "stepper-motors-drivers",
            Brand = "Autonics",
            Series = "MD Micro-step Drivers",
            NounEn = "Stepper Driver",
            NounBn = "স্টেপার ড্রাইভার",
            Header = "Model|Current|Supply|Price",
            Rows =
            [
                "MD2U-MD20|0.5–2.0 A/phase|24–40 V DC|9800",
                "MD5-HD14|0.35–1.4 A/phase (5-phase)|24 V DC|12500",
                "MD5-ND14|0.35–1.4 A/phase (5-phase)|24 V DC|13500"
            ],
            Title = "{Brand} {Model} Micro-step Stepper Driver, {Current}",
            Intro = "The Autonics {Model} is a micro-step driver delivering {Current} from a {Supply} supply. Step-direction input, automatic standstill current reduction and DIP-switch microstep selection give smooth, quiet motion on small axes.",
            Highlights = ["Drive current: {Current}", "Supply: {Supply}", "Pulse/direction input", "Automatic current-down at standstill"],
            Applications = ["Small CNC axes", "Indexing and dosing", "Laboratory automation"],
            Specs = ["General:Model=Model", "Electrical:Current=Phase current;Supply=Supply voltage"],
            Fixed = ["Control:Input=Step / direction (open-collector or line-driver)"],
            Shape = Shape.DinModule,
            WeightKg = 0.2m,
            BoxCm = (12m, 9m, 6m),
            Popularity = 0.5
        };
    }
}
