'use client';

import React from 'react';

export const SystemFaultIllustration: React.FC<React.SVGProps<SVGSVGElement>> = ({
  className = 'w-48 h-48',
  ...props
}) => (
  <svg
    viewBox="0 0 200 200"
    fill="none"
    xmlns="http://www.w3.org/2000/svg"
    className={className}
    {...props}
  >
    {/* Industrial Telemetry Server Rack */}
    <rect x="55" y="45" width="90" height="110" rx="6" stroke="currentColor" strokeWidth="2" className="text-text" />
    <line x1="55" y1="80" x2="145" y2="80" stroke="currentColor" strokeWidth="1.5" className="text-surface-border" />
    <line x1="55" y1="115" x2="145" y2="115" stroke="currentColor" strokeWidth="1.5" className="text-surface-border" />

    {/* Server bays indicator LEDs */}
    <circle cx="70" cy="62" r="3" fill="currentColor" className="text-status-online" />
    <circle cx="80" cy="62" r="3" fill="currentColor" className="text-surface-border" />
    <circle cx="70" cy="98" r="3" fill="currentColor" className="text-status-online" />

    {/* Fault Alert Badge overlaid on rack */}
    <polygon
      points="100,75 130,125 70,125"
      fill="currentColor"
      className="text-bg"
    />
    <polygon
      points="100,75 130,125 70,125"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinejoin="round"
      className="text-fault"
    />
    <line x1="100" y1="92" x2="100" y2="108" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" className="text-fault" />
    <circle cx="100" cy="116" r="1.5" fill="currentColor" className="text-fault" />

    {/* Severed circuit sparks */}
    <path
      d="M35 100L45 95L40 108L50 102"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      className="text-fault"
    />
    <path
      d="M165 100L155 95L160 108L150 102"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      className="text-fault"
    />
  </svg>
);
