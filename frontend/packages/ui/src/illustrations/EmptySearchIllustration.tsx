'use client';

import React from 'react';

export const EmptySearchIllustration: React.FC<React.SVGProps<SVGSVGElement>> = ({
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
    {/* Microchip Radar Target */}
    <circle
      cx="95"
      cy="90"
      r="45"
      stroke="currentColor"
      strokeWidth="2"
      className="text-text"
    />
    <circle
      cx="95"
      cy="90"
      r="30"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeDasharray="4 4"
      className="text-accent"
    />
    <circle cx="95" cy="90" r="3" fill="currentColor" className="text-accent" />

    {/* Crosshairs */}
    <line x1="95" y1="35" x2="95" y2="55" stroke="currentColor" strokeWidth="2" className="text-accent" />
    <line x1="95" y1="125" x2="95" y2="145" stroke="currentColor" strokeWidth="2" className="text-accent" />
    <line x1="40" y1="90" x2="60" y2="90" stroke="currentColor" strokeWidth="2" className="text-accent" />
    <line x1="130" y1="90" x2="150" y2="90" stroke="currentColor" strokeWidth="2" className="text-accent" />

    {/* Magnifier Handle */}
    <line
      x1="128"
      y1="123"
      x2="165"
      y2="160"
      stroke="currentColor"
      strokeWidth="8"
      strokeLinecap="round"
      className="text-text"
    />
    <line
      x1="135"
      y1="130"
      x2="158"
      y2="153"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      className="text-accent"
    />

    {/* Diagnostic Zero results signal */}
    <path
      d="M80 80L110 100M110 80L80 100"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinecap="round"
      className="text-surface-border-strong"
    />
  </svg>
);
