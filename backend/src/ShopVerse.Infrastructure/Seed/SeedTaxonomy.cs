namespace ShopVerse.Infrastructure.Seed;

internal sealed record LeafSeed(string NameEn, string NameBn, string Slug);
internal sealed record SubSeed(string NameEn, string NameBn, string Slug, LeafSeed[] Leaves);
internal sealed record RootSeed(string NameEn, string NameBn, string Slug, string Tagline, SubSeed[] Subs);

/// <summary>12 root categories → 3 sub-categories each → 2 leaves each (three levels, 72 leaves).</summary>
internal static class SeedTaxonomy
{
    public static readonly RootSeed[] Roots =
    [
        new("PLCs & Controllers", "পিএলসি ও কন্ট্রোলার", "plcs-controllers", "Programmable logic controllers, I/O and fieldbus modules",
        [
            new("Programmable Logic Controllers", "প্রোগ্রামেবল লজিক কন্ট্রোলার", "programmable-logic-controllers",
            [
                new("Compact PLCs", "কমপ্যাক্ট পিএলসি", "compact-plcs"),
                new("Micro & Nano PLCs", "মাইক্রো ও ন্যানো পিএলসি", "micro-nano-plcs")
            ]),
            new("I/O Modules", "আই/ও মডিউল", "io-modules",
            [
                new("Digital I/O Modules", "ডিজিটাল আই/ও মডিউল", "digital-io-modules"),
                new("Analog I/O Modules", "অ্যানালগ আই/ও মডিউল", "analog-io-modules")
            ]),
            new("Communication & Power", "কমিউনিকেশন ও পাওয়ার", "plc-communication-power",
            [
                new("PLC Communication Modules", "পিএলসি কমিউনিকেশন মডিউল", "plc-communication-modules"),
                new("PLC Power Supplies", "পিএলসি পাওয়ার সাপ্লাই", "plc-power-supplies")
            ])
        ]),
        new("HMI & Industrial PCs", "এইচএমআই ও ইন্ডাস্ট্রিয়াল পিসি", "hmi-industrial-pcs", "Touch panels, panel PCs and operator controls",
        [
            new("Touch Panels", "টাচ প্যানেল", "touch-panels",
            [
                new("Compact HMI Panels", "কমপ্যাক্ট এইচএমআই প্যানেল", "compact-hmi-panels"),
                new("Standard HMI Panels", "স্ট্যান্ডার্ড এইচএমআই প্যানেল", "standard-hmi-panels")
            ]),
            new("Industrial Computing", "ইন্ডাস্ট্রিয়াল কম্পিউটিং", "industrial-computing",
            [
                new("Panel PCs", "প্যানেল পিসি", "panel-pcs"),
                new("IIoT Gateways", "আইআইওটি গেটওয়ে", "iiot-gateways")
            ]),
            new("Operator Controls", "অপারেটর কন্ট্রোল", "operator-controls",
            [
                new("Pushbuttons & Selector Switches", "পুশবাটন ও সিলেক্টর সুইচ", "pushbuttons-selector-switches"),
                new("Indicator Lights & Beacons", "ইন্ডিকেটর লাইট ও বিকন", "indicator-lights-beacons")
            ])
        ]),
        new("Drives & Motion Control", "ড্রাইভ ও মোশন কন্ট্রোল", "drives-motion-control", "AC drives, servo systems, soft starters and steppers",
        [
            new("AC Drives", "এসি ড্রাইভ", "ac-drives",
            [
                new("Compact AC Drives", "কমপ্যাক্ট এসি ড্রাইভ", "compact-ac-drives"),
                new("General Purpose AC Drives", "জেনারেল পারপাস এসি ড্রাইভ", "general-purpose-ac-drives")
            ]),
            new("Servo Systems", "সার্ভো সিস্টেম", "servo-systems",
            [
                new("Servo Drives", "সার্ভো ড্রাইভ", "servo-drives"),
                new("Servo Motors", "সার্ভো মোটর", "servo-motors")
            ]),
            new("Starters & Steppers", "স্টার্টার ও স্টেপার", "starters-steppers",
            [
                new("Soft Starters", "সফট স্টার্টার", "soft-starters"),
                new("Stepper Motors & Drivers", "স্টেপার মোটর ও ড্রাইভার", "stepper-motors-drivers")
            ])
        ]),
        new("Sensors & Switches", "সেন্সর ও সুইচ", "sensors-switches", "Proximity, photoelectric, limit and encoder sensing",
        [
            new("Proximity Sensors", "প্রক্সিমিটি সেন্সর", "proximity-sensors",
            [
                new("Inductive Proximity Sensors", "ইন্ডাক্টিভ প্রক্সিমিটি সেন্সর", "inductive-proximity-sensors"),
                new("Capacitive Proximity Sensors", "ক্যাপাসিটিভ প্রক্সিমিটি সেন্সর", "capacitive-proximity-sensors")
            ]),
            new("Photoelectric Sensors", "ফটোইলেকট্রিক সেন্সর", "photoelectric-sensors",
            [
                new("Diffuse & Retro-reflective Sensors", "ডিফিউজ ও রেট্রো-রিফ্লেক্টিভ সেন্সর", "diffuse-retroreflective-sensors"),
                new("Fibre & Through-beam Sensors", "ফাইবার ও থ্রু-বিম সেন্সর", "fibre-through-beam-sensors")
            ]),
            new("Limit & Position", "লিমিট ও পজিশন", "limit-position",
            [
                new("Limit Switches", "লিমিট সুইচ", "limit-switches"),
                new("Rotary Encoders", "রোটারি এনকোডার", "rotary-encoders")
            ])
        ]),
        new("Motors & Gearboxes", "মোটর ও গিয়ারবক্স", "motors-gearboxes", "Induction motors, geared motors, fans and blowers",
        [
            new("AC Motors", "এসি মোটর", "ac-motors",
            [
                new("Three-phase Induction Motors", "থ্রি-ফেজ ইন্ডাকশন মোটর", "three-phase-induction-motors"),
                new("Single-phase Motors", "সিঙ্গেল-ফেজ মোটর", "single-phase-motors")
            ]),
            new("Geared Motors", "গিয়ারড মোটর", "geared-motors",
            [
                new("Helical Geared Motors", "হেলিক্যাল গিয়ারড মোটর", "helical-geared-motors"),
                new("Worm Geared Motors", "ওয়ার্ম গিয়ারড মোটর", "worm-geared-motors")
            ]),
            new("Fans & Blowers", "ফ্যান ও ব্লোয়ার", "fans-blowers",
            [
                new("Axial Cooling Fans", "অ্যাক্সিয়াল কুলিং ফ্যান", "axial-cooling-fans"),
                new("Centrifugal Blowers", "সেন্ট্রিফিউগাল ব্লোয়ার", "centrifugal-blowers")
            ])
        ]),
        new("Power Distribution & Protection", "পাওয়ার ডিস্ট্রিবিউশন ও প্রোটেকশন", "power-distribution-protection", "Breakers, contactors, overload relays and DIN-rail power",
        [
            new("Circuit Protection", "সার্কিট প্রোটেকশন", "circuit-protection",
            [
                new("Miniature Circuit Breakers", "মিনিয়েচার সার্কিট ব্রেকার", "miniature-circuit-breakers"),
                new("Moulded Case Circuit Breakers", "মোল্ডেড কেস সার্কিট ব্রেকার", "moulded-case-circuit-breakers")
            ]),
            new("Switching & Overload", "সুইচিং ও ওভারলোড", "switching-overload",
            [
                new("Power Contactors", "পাওয়ার কন্টাক্টর", "power-contactors"),
                new("Thermal Overload Relays", "থার্মাল ওভারলোড রিলে", "thermal-overload-relays")
            ]),
            new("Control Power & Relays", "কন্ট্রোল পাওয়ার ও রিলে", "control-power-relays",
            [
                new("DIN-rail Power Supplies", "ডিআইএন-রেল পাওয়ার সাপ্লাই", "din-rail-power-supplies"),
                new("Industrial Relays & Timers", "ইন্ডাস্ট্রিয়াল রিলে ও টাইমার", "relays-timers")
            ])
        ]),
        new("Pneumatics & Fluid Power", "নিউমেটিকস ও ফ্লুইড পাওয়ার", "pneumatics-fluid-power", "Cylinders, valves, air preparation and fittings",
        [
            new("Actuators", "অ্যাকচুয়েটর", "pneumatic-actuators",
            [
                new("ISO Cylinders", "আইএসও সিলিন্ডার", "iso-cylinders"),
                new("Compact Cylinders", "কমপ্যাক্ট সিলিন্ডার", "compact-cylinders")
            ]),
            new("Valves & Air Preparation", "ভালভ ও এয়ার প্রিপারেশন", "valves-air-preparation",
            [
                new("Solenoid Valves", "সলিনয়েড ভালভ", "solenoid-valves"),
                new("FRL Units", "এফআরএল ইউনিট", "frl-units")
            ]),
            new("Fittings & Tubing", "ফিটিংস ও টিউবিং", "fittings-tubing",
            [
                new("Push-in Fittings", "পুশ-ইন ফিটিংস", "push-in-fittings"),
                new("PU & Nylon Tubing", "পিইউ ও নাইলন টিউবিং", "pu-nylon-tubing")
            ])
        ]),
        new("Industrial Networking & Cables", "ইন্ডাস্ট্রিয়াল নেটওয়ার্কিং ও ক্যাবল", "networking-cables", "Ethernet switches, gateways, cables and connectors",
        [
            new("Industrial Ethernet", "ইন্ডাস্ট্রিয়াল ইথারনেট", "industrial-ethernet",
            [
                new("Managed Ethernet Switches", "ম্যানেজড ইথারনেট সুইচ", "managed-ethernet-switches"),
                new("Unmanaged Ethernet Switches", "আনম্যানেজড ইথারনেট সুইচ", "unmanaged-ethernet-switches")
            ]),
            new("Gateways & Serial", "গেটওয়ে ও সিরিয়াল", "gateways-serial",
            [
                new("Serial Device Servers", "সিরিয়াল ডিভাইস সার্ভার", "serial-device-servers"),
                new("Industrial Routers & Gateways", "ইন্ডাস্ট্রিয়াল রাউটার ও গেটওয়ে", "industrial-routers")
            ]),
            new("Cables & Connectors", "ক্যাবল ও কানেক্টর", "cables-connectors",
            [
                new("Control & Instrument Cables", "কন্ট্রোল ও ইন্সট্রুমেন্ট ক্যাবল", "control-instrument-cables"),
                new("M12 Connectors & Cordsets", "এম১২ কানেক্টর ও কর্ডসেট", "m12-connectors-cordsets")
            ])
        ]),
        new("Control Panels & Enclosures", "কন্ট্রোল প্যানেল ও এনক্লোজার", "control-panels-enclosures", "Enclosures, terminals, rails and wiring accessories",
        [
            new("Enclosures", "এনক্লোজার", "enclosures",
            [
                new("Wall-mount Steel Enclosures", "ওয়াল-মাউন্ট স্টিল এনক্লোজার", "wall-mount-steel-enclosures"),
                new("Polycarbonate & Stainless Enclosures", "পলিকার্বনেট ও স্টেইনলেস এনক্লোজার", "polycarbonate-stainless-enclosures")
            ]),
            new("Terminals & Rail", "টার্মিনাল ও রেল", "terminals-rail",
            [
                new("Terminal Blocks", "টার্মিনাল ব্লক", "terminal-blocks"),
                new("DIN Rails & Accessories", "ডিআইএন রেল ও অ্যাক্সেসরিজ", "din-rails-accessories")
            ]),
            new("Wiring Accessories", "ওয়্যারিং অ্যাক্সেসরিজ", "wiring-accessories",
            [
                new("Cable Ducts & Trunking", "ক্যাবল ডাক্ট ও ট্রাঙ্কিং", "cable-ducts-trunking"),
                new("Cable Glands & Lugs", "ক্যাবল গ্ল্যান্ড ও লাগ", "cable-glands-lugs")
            ])
        ]),
        new("Water, ETP & Process Instruments", "ওয়াটার, ইটিপি ও প্রসেস ইন্সট্রুমেন্ট", "water-etp-process", "Flow, level, analysers and dosing for water and effluent treatment",
        [
            new("Flow & Level", "ফ্লো ও লেভেল", "flow-level",
            [
                new("Electromagnetic Flow Meters", "ইলেকট্রোম্যাগনেটিক ফ্লো মিটার", "electromagnetic-flow-meters"),
                new("Ultrasonic Level Sensors", "আল্ট্রাসনিক লেভেল সেন্সর", "ultrasonic-level-sensors")
            ]),
            new("Water Analysers", "ওয়াটার অ্যানালাইজার", "water-analysers",
            [
                new("pH & ORP Analysers", "পিএইচ ও ওআরপি অ্যানালাইজার", "ph-orp-analysers"),
                new("Conductivity & DO Analysers", "কন্ডাক্টিভিটি ও ডিও অ্যানালাইজার", "conductivity-do-analysers")
            ]),
            new("Pumps", "পাম্প", "water-pumps",
            [
                new("Dosing Pumps", "ডোজিং পাম্প", "dosing-pumps"),
                new("Submersible & Booster Pumps", "সাবমার্সিবল ও বুস্টার পাম্প", "submersible-booster-pumps")
            ])
        ]),
        new("Process Measurement", "প্রসেস মেজারমেন্ট", "process-measurement", "Pressure, temperature, panel meters and controllers",
        [
            new("Pressure", "প্রেশার", "pressure-measurement",
            [
                new("Pressure Transmitters", "প্রেশার ট্রান্সমিটার", "pressure-transmitters"),
                new("Pressure Switches", "প্রেশার সুইচ", "pressure-switches")
            ]),
            new("Temperature", "তাপমাত্রা", "temperature-measurement",
            [
                new("RTD & Thermocouple Sensors", "আরটিডি ও থার্মোকাপল সেন্সর", "rtd-thermocouple-sensors"),
                new("Temperature Controllers", "টেম্পারেচার কন্ট্রোলার", "temperature-controllers")
            ]),
            new("Counters & Meters", "কাউন্টার ও মিটার", "counters-meters",
            [
                new("Digital Panel Meters", "ডিজিটাল প্যানেল মিটার", "digital-panel-meters"),
                new("Counters & Totalizers", "কাউন্টার ও টোটালাইজার", "counters-totalizers")
            ])
        ]),
        new("Machine Safety & Test Tools", "মেশিন সেফটি ও টেস্ট টুলস", "safety-test-tools", "Safety relays, E-stops, meters and panel thermal management",
        [
            new("Machine Safety", "মেশিন সেফটি", "machine-safety",
            [
                new("Emergency Stop & Safety Switches", "ইমার্জেন্সি স্টপ ও সেফটি সুইচ", "emergency-stop-safety-switches"),
                new("Safety Relays & Light Curtains", "সেফটি রিলে ও লাইট কার্টেন", "safety-relays-light-curtains")
            ]),
            new("Test & Measurement", "টেস্ট ও মেজারমেন্ট", "test-measurement",
            [
                new("Digital Multimeters", "ডিজিটাল মাল্টিমিটার", "digital-multimeters"),
                new("Clamp Meters & Insulation Testers", "ক্ল্যাম্প মিটার ও ইনসুলেশন টেস্টার", "clamp-insulation-testers")
            ]),
            new("Panel Thermal Management", "প্যানেল থার্মাল ম্যানেজমেন্ট", "panel-thermal-management",
            [
                new("Enclosure Fans & Filters", "এনক্লোজার ফ্যান ও ফিল্টার", "enclosure-fans-filters"),
                new("Panel Heaters & Thermostats", "প্যানেল হিটার ও থার্মোস্ট্যাট", "panel-heaters-thermostats")
            ])
        ])
    ];

    public static IEnumerable<(RootSeed Root, SubSeed Sub, LeafSeed Leaf)> Leaves() =>
        Roots.SelectMany(r => r.Subs.SelectMany(s => s.Leaves.Select(l => (r, s, l))));
}
