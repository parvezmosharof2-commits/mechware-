import React from 'react';

interface HardwareVectorArtProps {
  type: 'plc' | 'vfd' | 'hmi' | 'servo' | 'skid' | 'sensor';
  sku: string;
}

export const HardwareVectorArt: React.FC<HardwareVectorArtProps> = ({ type, sku }) => {
  return (
    <div className="relative w-full h-full bg-[#0A1128] flex flex-col items-center justify-center p-6 overflow-hidden select-none">
      {/* Subtle engineering blueprint grid */}
      <div
        className="absolute inset-0 opacity-15 pointer-events-none"
        style={{
          backgroundImage:
            'linear-gradient(to right, #1B2B55 1px, transparent 1px), linear-gradient(to bottom, #1B2B55 1px, transparent 1px)',
          backgroundSize: '20px 20px',
        }}
      />

      {type === 'plc' && (
        <svg viewBox="0 0 260 180" className="w-4/5 h-4/5 drop-shadow">
          {/* DIN Rail */}
          <rect x="10" y="75" width="240" height="30" fill="#101B3B" stroke="#2A4078" strokeWidth="1.5" />
          {/* Main PLC Housing */}
          <rect x="35" y="20" width="190" height="140" rx="6" fill="#0D1635" stroke="#00E5FF" strokeWidth="1.5" />
          {/* Top Terminal Block (24V DC + 14 DI) */}
          <rect x="48" y="28" width="164" height="22" rx="2" fill="#060B18" stroke="#2A4078" strokeWidth="1" />
          {Array.from({ length: 12 }).map((_, i) => (
            <circle key={`di-${i}`} cx={57 + i * 13} cy="39" r="3.2" fill="#101B3B" stroke="#94A3B8" strokeWidth="1" />
          ))}
          {/* Center Status & Diagnostic Matrix */}
          <rect x="48" y="58" width="95" height="62" rx="3" fill="#060B18" stroke="#1B2B55" strokeWidth="1" />
          <text x="56" y="74" fill="#00E5FF" fontSize="9" fontFamily="JetBrains Mono" fontWeight="600">
            SIMATIC S7-1200
          </text>
          <text x="56" y="87" fill="#94A3B8" fontSize="7.5" fontFamily="JetBrains Mono">
            CPU 1214C DC/DC/DC
          </text>
          <rect x="56" y="96" width="32" height="14" rx="2" fill="#101B3B" stroke="#10B981" strokeWidth="1" />
          <text x="61" y="106" fill="#10B981" fontSize="7" fontFamily="JetBrains Mono">
            RUN
          </text>
          {/* Dual RJ45 Profinet Ports */}
          <rect x="155" y="62" width="54" height="54" rx="3" fill="#060B18" stroke="#2A4078" strokeWidth="1" />
          <rect x="165" y="70" width="34" height="22" rx="2" fill="#101B3B" stroke="#00E5FF" strokeWidth="1" />
          <text x="165" y="106" fill="#94A3B8" fontSize="7" fontFamily="JetBrains Mono">
            PROFINET
          </text>
          {/* Bottom Terminal Block (10 DO) */}
          <rect x="48" y="130" width="164" height="22" rx="2" fill="#060B18" stroke="#2A4078" strokeWidth="1" />
          {Array.from({ length: 10 }).map((_, i) => (
            <circle key={`do-${i}`} cx={60 + i * 15} cy="141" r="3.2" fill="#101B3B" stroke="#94A3B8" strokeWidth="1" />
          ))}
        </svg>
      )}

      {type === 'vfd' && (
        <svg viewBox="0 0 260 180" className="w-4/5 h-4/5">
          {/* VFD Vertical Chassis */}
          <rect x="65" y="12" width="130" height="156" rx="6" fill="#0D1635" stroke="#00E5FF" strokeWidth="1.5" />
          {/* Heatsink Top Fins */}
          {Array.from({ length: 8 }).map((_, i) => (
            <line
              key={`fin-${i}`}
              x1={78 + i * 15}
              y1="12"
              x2={78 + i * 15}
              y2="26"
              stroke="#2A4078"
              strokeWidth="2"
            />
          ))}
          {/* Digital Keypad Readout */}
          <rect x="85" y="34" width="90" height="44" rx="4" fill="#060B18" stroke="#2A4078" strokeWidth="1.2" />
          <text x="95" y="61" fill="#00E5FF" fontSize="18" fontFamily="JetBrains Mono" fontWeight="700">
            50.00Hz
          </text>
          <text x="95" y="72" fill="#94A3B8" fontSize="6.5" fontFamily="JetBrains Mono">
            FWD · VFD-MS300 · 415V
          </text>
          {/* Control Buttons */}
          <rect x="85" y="86" width="26" height="16" rx="2" fill="#10B981" fillOpacity="0.2" stroke="#10B981" strokeWidth="1" />
          <text x="90" y="97" fill="#34D399" fontSize="7" fontFamily="JetBrains Mono">
            RUN
          </text>
          <rect x="117" y="86" width="26" height="16" rx="2" fill="#101B3B" stroke="#2A4078" strokeWidth="1" />
          <text x="121" y="97" fill="#F8FAFC" fontSize="7" fontFamily="JetBrains Mono">
            MODE
          </text>
          <rect x="149" y="86" width="26" height="16" rx="2" fill="#DC2626" fillOpacity="0.2" stroke="#F87171" strokeWidth="1" />
          <text x="153" y="97" fill="#F87171" fontSize="7" fontFamily="JetBrains Mono">
            STOP
          </text>
          {/* High Voltage Terminal Guard */}
          <rect x="78" y="114" width="104" height="42" rx="3" fill="#060B18" stroke="#1B2B55" strokeWidth="1" />
          <text x="90" y="132" fill="#94A3B8" fontSize="8" fontFamily="JetBrains Mono">
            R / S / T · U / V / W
          </text>
          <text x="90" y="146" fill="#64748B" fontSize="7" fontFamily="JetBrains Mono">
            STO SIL2 INTEGRATED
          </text>
        </svg>
      )}

      {type === 'skid' && (
        <svg viewBox="0 0 260 180" className="w-4/5 h-4/5">
          {/* Skid Base Frame */}
          <rect x="20" y="150" width="220" height="12" rx="2" fill="#1A5CB0" stroke="#00E5FF" strokeWidth="1.2" />
          {/* Control Panel Left */}
          <rect x="30" y="28" width="86" height="122" rx="4" fill="#0D1635" stroke="#00E5FF" strokeWidth="1.5" />
          {/* Embedded HMI Screen */}
          <rect x="42" y="42" width="62" height="40" rx="2" fill="#060B18" stroke="#00E5FF" strokeWidth="1" />
          <polyline points="46,72 58,58 70,64 84,50 98,54" fill="none" stroke="#00E5FF" strokeWidth="1.5" />
          <text x="46" y="96" fill="#F8FAFC" fontSize="7.5" fontFamily="JetBrains Mono">
            MECHWARE SCADA
          </text>
          <text x="46" y="108" fill="#34D399" fontSize="7" fontFamily="JetBrains Mono">
            pH: 7.2 · DO: 6.4
          </text>
          {/* Piping & Dosing Manifold Right */}
          <rect x="140" y="52" width="84" height="98" rx="4" fill="#060B18" stroke="#2A4078" strokeWidth="1.2" />
          <line x1="116" y1="85" x2="140" y2="85" stroke="#00E5FF" strokeWidth="3" />
          <circle cx="165" cy="85" r="14" fill="#101B3B" stroke="#00E5FF" strokeWidth="1.5" />
          <circle cx="200" cy="85" r="14" fill="#101B3B" stroke="#00E5FF" strokeWidth="1.5" />
          <text x="150" y="125" fill="#94A3B8" fontSize="7.5" fontFamily="JetBrains Mono">
            SS316L MANIFOLD
          </text>
          <text x="150" y="138" fill="#00E5FF" fontSize="7" fontFamily="JetBrains Mono">
            MADE IN BANGLADESH
          </text>
        </svg>
      )}

      {type === 'servo' && (
        <svg viewBox="0 0 260 180" className="w-4/5 h-4/5">
          {/* Servo Amplifier Left */}
          <rect x="28" y="24" width="90" height="132" rx="4" fill="#0D1635" stroke="#00E5FF" strokeWidth="1.5" />
          <rect x="40" y="36" width="66" height="26" rx="2" fill="#060B18" stroke="#2A4078" strokeWidth="1" />
          <text x="46" y="53" fill="#00E5FF" fontSize="10" fontFamily="JetBrains Mono" fontWeight="600">
            MR-J4-A
          </text>
          <text x="40" y="80" fill="#94A3B8" fontSize="7.5" fontFamily="JetBrains Mono">
            2.5kHz RESPONSE
          </text>
          <text x="40" y="94" fill="#34D399" fontSize="7.5" fontFamily="JetBrains Mono">
            22-BIT ENCODER
          </text>
          {/* Servo Motor Right */}
          <rect x="142" y="56" width="76" height="72" rx="6" fill="#101B3B" stroke="#2A4078" strokeWidth="1.5" />
          <rect x="130" y="66" width="12" height="52" fill="#0D1635" stroke="#00E5FF" strokeWidth="1.2" />
          {/* Motor Shaft */}
          <rect x="108" y="86" width="22" height="12" rx="2" fill="#94A3B8" />
          <text x="152" y="96" fill="#F8FAFC" fontSize="8.5" fontFamily="JetBrains Mono" fontWeight="600">
            MELSERVO
          </text>
        </svg>
      )}

      {type === 'sensor' && (
        <svg viewBox="0 0 260 180" className="w-4/5 h-4/5">
          {/* Cylindrical M18 Threaded Body */}
          <rect x="55" y="68" width="130" height="44" rx="4" fill="#101B3B" stroke="#00E5FF" strokeWidth="1.5" />
          {/* Sensing Face */}
          <rect x="42" y="72" width="13" height="36" rx="2" fill="#00E5FF" fillOpacity="0.25" stroke="#00E5FF" strokeWidth="1.5" />
          {/* Hex Lock Nuts */}
          <rect x="90" y="56" width="16" height="68" rx="2" fill="#0D1635" stroke="#94A3B8" strokeWidth="1.2" />
          <rect x="135" y="56" width="16" height="68" rx="2" fill="#0D1635" stroke="#94A3B8" strokeWidth="1.2" />
          {/* IO-Link Cable End */}
          <path d="M 185 90 Q 225 90 235 125" fill="none" stroke="#2A4078" strokeWidth="8" strokeLinecap="round" />
          {/* Sensing Magnetic Field Waves */}
          <path d="M 30 74 Q 18 90 30 106" fill="none" stroke="#00E5FF" strokeWidth="1.5" strokeDasharray="3 3" />
          <path d="M 20 66 Q 4 90 20 114" fill="none" stroke="#00E5FF" strokeWidth="1.2" strokeDasharray="3 3" />
          <text x="65" y="145" fill="#94A3B8" fontSize="8" fontFamily="JetBrains Mono">
            OMRON E2E NEXT · IP69K IO-LINK
          </text>
        </svg>
      )}

      {type === 'hmi' && (
        <svg viewBox="0 0 260 180" className="w-4/5 h-4/5">
          {/* Widescreen Bezel */}
          <rect x="24" y="24" width="212" height="132" rx="8" fill="#0D1635" stroke="#00E5FF" strokeWidth="1.5" />
          {/* Active TFT Display */}
          <rect x="38" y="36" width="184" height="108" rx="3" fill="#060B18" stroke="#2A4078" strokeWidth="1" />
          {/* Telemetry UI inside HMI */}
          <rect x="48" y="46" width="76" height="42" rx="2" fill="#101B3B" />
          <text x="54" y="62" fill="#94A3B8" fontSize="7" fontFamily="JetBrains Mono">
            LINE SPEED
          </text>
          <text x="54" y="78" fill="#00E5FF" fontSize="13" fontFamily="JetBrains Mono" fontWeight="700">
            300 PPM
          </text>
          <rect x="132" y="46" width="80" height="42" rx="2" fill="#101B3B" />
          <text x="138" y="62" fill="#94A3B8" fontSize="7" fontFamily="JetBrains Mono">
            BOILER BAR
          </text>
          <text x="138" y="78" fill="#34D399" fontSize="13" fontFamily="JetBrains Mono" fontWeight="700">
            8.4 BAR
          </text>
          <polyline
            points="48,128 75,112 102,118 130,102 158,108 185,98 212,104"
            fill="none"
            stroke="#00E5FF"
            strokeWidth="2"
          />
        </svg>
      )}

      <div className="mt-2 w-full flex items-center justify-between text-[11px] text-[#64748B] font-mono-tabular">
        <span>{sku}</span>
        <span>CAD · STEP · PDF</span>
      </div>
    </div>
  );
};
