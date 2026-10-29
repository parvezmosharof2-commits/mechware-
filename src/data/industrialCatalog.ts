export interface TechnicalSpecGroup {
  group: string;
  items: { label: string; value: string }[];
}

export interface ProductVariantOption {
  id: string;
  sku: string;
  label: string;
  voltage: string;
  ioOrPower: string;
  priceBdt: number;
  compareAtBdt?: number;
  onHand: number;
  reserved: number;
}

export interface IndustrialProduct {
  id: string;
  slug: string;
  titleEn: string;
  titleBn: string;
  brand: 'Siemens' | 'Mitsubishi Electric' | 'Delta' | 'Omron' | 'MECHWARE Direct';
  category: string;
  categorySlug: string;
  sellerName: string;
  sellerVerified: boolean;
  commissionRate: number;
  artworkType: 'plc' | 'vfd' | 'hmi' | 'servo' | 'skid' | 'sensor';
  shortSummary: string;
  warrantyMonths: number;
  leadTimePromise: string;
  codEligible: boolean;
  rfqEligible: boolean;
  datasheetCode: string;
  cadStepFile: string;
  rating: number;
  reviewCount: number;
  highlights: string[];
  specs: TechnicalSpecGroup[];
  variants: ProductVariantOption[];
}

export const INDUSTRIAL_CATALOG: IndustrialProduct[] = [
  {
    id: 'prod-s7-1200',
    slug: 'siemens-simatic-s7-1200-cpu-1214c-dc-dc-dc',
    titleEn: 'Siemens SIMATIC S7-1200 CPU 1214C Compact Controller',
    titleBn: 'সিমেন্স সিমেটিক S7-1200 CPU 1214C অটোমেশন কন্ট্রোলার',
    brand: 'Siemens',
    category: 'PLCs & Automation Controllers',
    categorySlug: 'plcs-controllers',
    sellerName: 'Siemens Authorized Automation BD',
    sellerVerified: true,
    commissionRate: 0.08,
    artworkType: 'plc',
    shortSummary: 'Modular compact PLC with integrated PROFINET IO controller, 14 DI / 10 DO / 2 AI onboard, high-speed pulse outputs for motion control.',
    warrantyMonths: 24,
    leadTimePromise: 'Order within 3h 20m · Dispatch Today to Dhaka & Gazipur EPZ',
    codEligible: false,
    rfqEligible: true,
    datasheetCode: 'DS-6ES7214-1AG40-0XB0.pdf',
    cadStepFile: 'CAD-S7-1214C-STEP-V4.step',
    rating: 4.9,
    reviewCount: 142,
    highlights: [
      'Integrated PROFINET interface for programming, HMI networking, and PLC-to-PLC communication',
      'Onboard 14 Digital Inputs (24V DC sink/source) & 10 Transistor Outputs (0.5A)',
      '6 High-Speed Counters (up to 100 kHz) and 4 PTO/PWM outputs for stepper/servo control',
      'Expandable with up to 8 signal modules and 3 communication modules (RS485/Modbus RTU)'
    ],
    specs: [
      {
        group: 'Electrical & Power Supply',
        items: [
          { label: 'Supply Voltage', value: '20.4V – 28.8V DC (24V DC Nominal)' },
          { label: 'Inrush Current Max', value: '12 A at 28.8V DC' },
          { label: 'Isolation Voltage', value: '500V AC (Channel to Ground)' }
        ]
      },
      {
        group: 'Memory & Processing',
        items: [
          { label: 'Work Memory', value: '100 KB Integrated (Non-retentive)' },
          { label: 'Load Memory', value: '4 MB Integrated + SIMATIC Memory Card' },
          { label: 'Bit Execution Speed', value: '0.08 µs / instruction' }
        ]
      },
      {
        group: 'Environmental & Compliance',
        items: [
          { label: 'Protection Rating', value: 'IP20 (DIN Rail 35mm Mount)' },
          { label: 'Ambient Operating Temp', value: '-20°C to +60°C (Horizontal)' },
          { label: 'Certifications', value: 'CE, UL, cULus, RCM, Marine DNV-GL' }
        ]
      }
    ],
    variants: [
      {
        id: 'var-s7-1214c-dc',
        sku: 'MW-SIE-6ES7214-1AG40',
        label: 'CPU 1214C DC/DC/DC (Transistor)',
        voltage: '24V DC',
        ioOrPower: '14 DI / 10 DO / 2 AI',
        priceBdt: 44500,
        compareAtBdt: 49000,
        onHand: 18,
        reserved: 3
      },
      {
        id: 'var-s7-1214c-rly',
        sku: 'MW-SIE-6ES7214-1BG40',
        label: 'CPU 1214C AC/DC/Relay',
        voltage: '85–264V AC',
        ioOrPower: '14 DI / 10 Relay DO',
        priceBdt: 46200,
        compareAtBdt: 51000,
        onHand: 9,
        reserved: 2
      }
    ]
  },
  {
    id: 'prod-delta-vfd',
    slug: 'delta-ms300-compact-vector-control-drive-vfd',
    titleEn: 'Delta MS300 Series Compact Vector Control VFD Inverter',
    titleBn: 'ডেল্টা MS300 থ্রি-ফেজ মোটর ইনভার্টার (VFD)',
    brand: 'Delta',
    category: 'VFDs & Motor Inverters',
    categorySlug: 'vfds-inverters',
    sellerName: 'MECHWARE Direct',
    sellerVerified: true,
    commissionRate: 0.0,
    artworkType: 'vfd',
    shortSummary: 'High-efficiency AC motor drive supporting both IM and PM motors, built-in PLC (2k steps), brake chopper, and RS-485 Modbus RTU.',
    warrantyMonths: 18,
    leadTimePromise: 'In Stock at Gulshan Hub · Next-Day Delivery Across Dhaka',
    codEligible: true,
    rfqEligible: true,
    datasheetCode: 'DS-DELTA-VFD-MS300-EN.pdf',
    cadStepFile: 'CAD-MS300-FRAME-B.step',
    rating: 4.8,
    reviewCount: 96,
    highlights: [
      'Supports open-loop vector control for Induction (IM) and Permanent Magnet (PM) motors',
      'Built-in 2,000-step PLC logic capacity eliminating external mini-controllers for pump skids',
      '150% starting torque at 0.5 Hz in Sensorless Vector Control mode',
      'Conformal PCB coating (IEC 60721-3-3 Class 3C2) for humid Bangladeshi textile environments'
    ],
    specs: [
      {
        group: 'Drive Output & Control',
        items: [
          { label: 'Input Supply', value: '3-Phase 380V – 480V AC (-15% to +10%), 50/60 Hz' },
          { label: 'Output Frequency Range', value: '0.00 to 599.00 Hz' },
          { label: 'Overload Capacity', value: '120% for 60s (Normal Duty) / 150% for 60s (Heavy Duty)' }
        ]
      },
      {
        group: 'Communications & Safety',
        items: [
          { label: 'Fieldbus Protocol', value: 'Built-in Modbus RTU/ASCII (RS-485) + Optional CANopen/Profibus' },
          { label: 'Safety Standard', value: 'Integrated Safe Torque Off (STO SIL2 / PLd)' }
        ]
      }
    ],
    variants: [
      {
        id: 'var-delta-075kw',
        sku: 'MW-DLT-VFD2A8MS43ANSAA',
        label: '0.75 kW (1 HP) · 3-Phase 415V',
        voltage: '415V AC 3Φ',
        ioOrPower: '0.75 kW / 2.8A',
        priceBdt: 13800,
        compareAtBdt: 15500,
        onHand: 28,
        reserved: 4
      },
      {
        id: 'var-delta-22kw',
        sku: 'MW-DLT-VFD5A5MS43ANSAA',
        label: '2.2 kW (3 HP) · 3-Phase 415V',
        voltage: '415V AC 3Φ',
        ioOrPower: '2.2 kW / 5.5A',
        priceBdt: 21500,
        compareAtBdt: 24000,
        onHand: 14,
        reserved: 1
      },
      {
        id: 'var-delta-55kw',
        sku: 'MW-DLT-VFD13AMS43ANSAA',
        label: '5.5 kW (7.5 HP) · 3-Phase 415V',
        voltage: '415V AC 3Φ',
        ioOrPower: '5.5 kW / 13A',
        priceBdt: 38900,
        compareAtBdt: 42500,
        onHand: 7,
        reserved: 2
      }
    ]
  },
  {
    id: 'prod-mechware-etp-skid',
    slug: 'mechware-smart-etp-chemical-dosing-scada-skid',
    titleEn: 'MECHWARE Automated ETP Chemical Dosing & Cloud SCADA Skid',
    titleBn: 'মেকওয়্যার অটোমেটেড ইটিপি ডোজিং ও ক্লাউড স্ক্যাডা স্কিড (Made in Bangladesh)',
    brand: 'MECHWARE Direct',
    category: 'Made in Bangladesh Machinery',
    categorySlug: 'made-in-bd-machinery',
    sellerName: 'MECHWARE Direct',
    sellerVerified: true,
    commissionRate: 0.0,
    artworkType: 'skid',
    shortSummary: 'Locally manufactured turnkey Effluent Treatment Plant (ETP) dosing & real-time DoE compliance monitoring skid deployed at Square & Naturub.',
    warrantyMonths: 36,
    leadTimePromise: 'Custom Engineered in Dhaka · 14-Day Commissioning SLA',
    codEligible: false,
    rfqEligible: true,
    datasheetCode: 'DS-MW-ETP-SCADA-2026.pdf',
    cadStepFile: 'CAD-MW-ETP-SKID-REV3.step',
    rating: 5.0,
    reviewCount: 29,
    highlights: [
      'Designed & fabricated in Dhaka to Department of Environment (DoE) online monitoring standards',
      'Closed-loop PID chemical dosing using industrial pH, ORP, Dissolved Oxygen (DO), and电磁 flow meters',
      'Siemens S7-1200 PLC core + 10.1" IP65 Industrial Touch HMI + MQTT/OPC-UA Cloud Telemetry',
      'Reduces chemical consumption by 18–26% with automated coagulant/flocculant batching'
    ],
    specs: [
      {
        group: 'Process & Hydraulic Specs',
        items: [
          { label: 'Dosing Capacity', value: '50 L/hr to 1,200 L/hr (Dual Redundant Diaphragm Pumps)' },
          { label: 'Manifold Material', value: 'SS316L / UPVC Schedule 80 Corrosion-Resistant' },
          { label: 'Sensor Inputs', value: '4–20mA Hart + RS485 Modbus (pH, COD, BOD, TSS, Flow)' }
        ]
      },
      {
        group: 'Control & Telemetry',
        items: [
          { label: 'Primary Controller', value: 'Siemens SIMATIC S7-1200 / Delta AS300 Configurable' },
          { label: 'Enclosure Rating', value: 'IP65 Powder-Coated 14SWG Panel with Rittal Cooling' }
        ]
      }
    ],
    variants: [
      {
        id: 'var-mw-etp-50m3',
        sku: 'MW-BD-ETP-SKID-50M3',
        label: '50 m³/hr ETP Skid (Delta PLC + 7" HMI)',
        voltage: '415V AC 3Φ',
        ioOrPower: '50 m³/hr Capacity',
        priceBdt: 385000,
        compareAtBdt: 420000,
        onHand: 4,
        reserved: 1
      },
      {
        id: 'var-mw-etp-150m3',
        sku: 'MW-BD-ETP-SKID-150M3',
        label: '150 m³/hr Turnkey Skid (Siemens S7-1200 + Cloud SCADA)',
        voltage: '415V AC 3Φ',
        ioOrPower: '150 m³/hr + DoE Link',
        priceBdt: 640000,
        compareAtBdt: 695000,
        onHand: 2,
        reserved: 0
      }
    ]
  },
  {
    id: 'prod-mitsubishi-servo',
    slug: 'mitsubishi-melservo-j4-ac-servo-amplifier-motor-kit',
    titleEn: 'Mitsubishi MELSERVO-J4 2.5kHz AC Servo Drive & Encoder Motor Kit',
    titleBn: 'মিতসুবিশি MELSERVO-J4 এসি সার্ভো ড্রাইভ ও মোটর কিট',
    brand: 'Mitsubishi Electric',
    category: 'Servo Drives & Motion Control',
    categorySlug: 'servo-motion-control',
    sellerName: 'Dhaka Motion & Robotics Ltd.',
    sellerVerified: true,
    commissionRate: 0.08,
    artworkType: 'servo',
    shortSummary: 'Ultra-precision 22-bit absolute encoder (4,194,304 pulses/rev) servo system for high-speed packaging, rotary knife cutting, and CNC axes.',
    warrantyMonths: 24,
    leadTimePromise: 'Order within 5h · Express Courier to Dhaka, Chattogram & Narayanganj',
    codEligible: false,
    rfqEligible: true,
    datasheetCode: 'DS-MIT-MR-J4-70B.pdf',
    cadStepFile: 'CAD-HG-KR73-SERVO.step',
    rating: 4.9,
    reviewCount: 64,
    highlights: [
      'Industry-leading 2.5 kHz speed frequency response for sub-millisecond settling time',
      '22-bit high-resolution absolute/incremental encoder (4.19 million pulses per revolution)',
      'One-touch auto-tuning with real-time machine resonance suppression filter',
      'Proven in 300 PPM duplex paper cutting & high-speed pouch packaging lines across Bangladesh'
    ],
    specs: [
      {
        group: 'Motion Performance',
        items: [
          { label: 'Encoder Resolution', value: '22-bit (4,194,304 pulses/rev)' },
          { label: 'Speed Frequency Response', value: '2,500 Hz' },
          { label: 'Command Interface', value: 'SSCNET III/H Optical / Pulse Train / Modbus' }
        ]
      }
    ],
    variants: [
      {
        id: 'var-mit-400w',
        sku: 'MW-MIT-MR-J4-40A-KIT',
        label: '400W Kit (MR-J4-40A + HG-KR43 + 5m Cables)',
        voltage: '200V AC',
        ioOrPower: '400W / 3000 RPM',
        priceBdt: 52000,
        compareAtBdt: 57500,
        onHand: 11,
        reserved: 2
      },
      {
        id: 'var-mit-750w',
        sku: 'MW-MIT-MR-J4-70A-KIT',
        label: '750W Kit (MR-J4-70A + HG-KR73B Electromagnetic Brake)',
        voltage: '200V AC',
        ioOrPower: '750W / Brake',
        priceBdt: 68500,
        compareAtBdt: 74000,
        onHand: 6,
        reserved: 1
      }
    ]
  },
  {
    id: 'prod-omron-sensor',
    slug: 'omron-e2e-next-long-distance-inductive-proximity-sensor',
    titleEn: 'Omron E2E NEXT Series Quadruple-Distance Inductive Proximity Sensor',
    titleBn: 'ওমরন E2E NEXT ইন্ডাক্টিভ প্রক্সিমিটি সেন্সর (IP69K)',
    brand: 'Omron',
    category: 'Smart Sensors & Instrumentation',
    categorySlug: 'smart-sensors',
    sellerName: 'Siemens Authorized Automation BD',
    sellerVerified: true,
    commissionRate: 0.08,
    artworkType: 'sensor',
    shortSummary: 'Thermal-detecting IC yields 4× sensing range on M12/M18 brass nickel-plated housings with oil-resistant PVC cable and IO-Link.',
    warrantyMonths: 12,
    leadTimePromise: 'Same-Day Dispatch · Cash on Delivery Eligible (<= 15,000 BDT)',
    codEligible: true,
    rfqEligible: false,
    datasheetCode: 'DS-OMRON-E2E-NEXT.pdf',
    cadStepFile: 'CAD-E2E-X10MY1-M18.step',
    rating: 4.9,
    reviewCount: 215,
    highlights: [
      'Quadruple sensing distance prevents mechanical collision damage on vibrating conveyor lines',
      'IP67, IP67G (oil-resistant), and IP69K high-pressure washdown certified for food & pharma',
      '360° high-visibility orange ring LED indicator for instant commissioning diagnostics',
      'Eligible for instant Cash on Delivery (COD) with SMS OTP verification'
    ],
    specs: [
      {
        group: 'Sensing & Electrical',
        items: [
          { label: 'Sensing Distance', value: '8 mm (M12 Shielded) / 14 mm (M18 Shielded)' },
          { label: 'Output Configuration', value: 'PNP Normally Open (NO) + IO-Link COM3' },
          { label: 'Operating Voltage', value: '10 to 30V DC (Ripple < 10%)' }
        ]
      }
    ],
    variants: [
      {
        id: 'var-omr-m12',
        sku: 'MW-OMR-E2EW-QX7B1T12',
        label: 'M12 Shielded · 7mm Range · PNP NO (2m Pre-wired)',
        voltage: '10–30V DC',
        ioOrPower: 'M12 / 7mm PNP',
        priceBdt: 4250,
        compareAtBdt: 4800,
        onHand: 64,
        reserved: 5
      },
      {
        id: 'var-omr-m18',
        sku: 'MW-OMR-E2EW-QX14B1T18',
        label: 'M18 Shielded · 14mm Range · PNP NO + IO-Link',
        voltage: '10–30V DC',
        ioOrPower: 'M18 / 14mm IO-Link',
        priceBdt: 5600,
        compareAtBdt: 6200,
        onHand: 42,
        reserved: 3
      }
    ]
  },
  {
    id: 'prod-delta-hmi',
    slug: 'delta-dop-110ws-10-inch-widescreen-industrial-touch-hmi',
    titleEn: 'Delta DOP-110WS 10.1" Cortex-A8 Widescreen Industrial Touch HMI',
    titleBn: 'ডেল্টা DOP-110WS ১০.১" ইন্ডাস্ট্রিয়াল টাচ ডিসপ্লে (HMI)',
    brand: 'Delta',
    category: 'HMIs & Touch Displays',
    categorySlug: 'hmis-displays',
    sellerName: 'MECHWARE Direct',
    sellerVerified: true,
    commissionRate: 0.0,
    artworkType: 'hmi',
    shortSummary: '1024×600 high-brightness TFT LCD operator panel with 800MHz ARM Cortex-A8 processor, dual Ethernet/serial ports, VNC remote monitoring, and IP65 bezel.',
    warrantyMonths: 18,
    leadTimePromise: 'In Stock at Gulshan Hub · Next-Day Delivery Across Dhaka',
    codEligible: false,
    rfqEligible: true,
    datasheetCode: 'DS-DELTA-DOP-110WS.pdf',
    cadStepFile: 'CAD-DOP-110WS-PANEL.step',
    rating: 4.8,
    reviewCount: 83,
    highlights: [
      '10.1-inch (1024 × 600 px) 65,536-color TFT LCD with 450 cd/m² industrial backlight',
      'Built-in Ethernet port + 3 COM ports supporting RS-232 / RS-422 / RS-485 multi-drop',
      'Native driver support for Siemens S7-1200/1500, Mitsubishi FX/Q, Omron, and Modbus TCP/RTU',
      'Supports VNC Viewer mobile remote monitoring and FTP recipe alarm log export'
    ],
    specs: [
      {
        group: 'Display & Processor',
        items: [
          { label: 'Display Resolution', value: '1024 × 600 WSVGA 10.1" TFT' },
          { label: 'CPU & Memory', value: 'ARM Cortex-A8 (800 MHz) · 256 MB RAM · 256 MB ROM' },
          { label: 'Front Panel Protection', value: 'IP65 / NEMA 4 Waterproof Front Bezel' }
        ]
      }
    ],
    variants: [
      {
        id: 'var-hmi-107bv',
        sku: 'MW-DLT-DOP-107BV',
        label: '7.0" Standard (800×480) · RS485/RS232',
        voltage: '24V DC',
        ioOrPower: '7.0" WVGA',
        priceBdt: 14500,
        compareAtBdt: 16200,
        onHand: 22,
        reserved: 2
      },
      {
        id: 'var-hmi-110ws',
        sku: 'MW-DLT-DOP-110WS',
        label: '10.1" Ethernet + 3x COM (1024×600) · VNC Enabled',
        voltage: '24V DC',
        ioOrPower: '10.1" Ethernet',
        priceBdt: 28500,
        compareAtBdt: 31500,
        onHand: 15,
        reserved: 1
      }
    ]
  }
];
