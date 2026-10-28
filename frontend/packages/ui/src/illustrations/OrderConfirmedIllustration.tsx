'use client';

import React from 'react';

export const OrderConfirmedIllustration: React.FC<React.SVGProps<SVGSVGElement>> = ({
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
    {/* Concentric success radar waves */}
    <circle cx="100" cy="100" r="75" stroke="currentColor" strokeWidth="1" strokeDasharray="3 3" className="text-status-online opacity-40" />
    <circle cx="100" cy="100" r="60" stroke="currentColor" strokeWidth="1.5" className="text-status-online opacity-70" />

    {/* Industrial Packaging Crate / Module */}
    <rect x="65" y="70" width="70" height="65" rx="6" stroke="currentColor" strokeWidth="2" className="text-text" />
    <line x1="65" y1="90" x2="135" y2="90" stroke="currentColor" strokeWidth="1.5" className="text-surface-border" />
    <line x1="100" y1="90" x2="100" y2="135" stroke="currentColor" strokeWidth="1.5" className="text-surface-border" />

    {/* Verification Badge */}
    <circle cx="100" cy="65" r="22" fill="currentColor" className="text-bg" />
    <circle cx="100" cy="65" r="22" stroke="currentColor" strokeWidth="2.5" className="text-status-online" />
    <path
      d="M91 65L97 71L110 57"
      stroke="currentColor"
      strokeWidth="3"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="text-status-online"
    />

    {/* Robotic Conveyor Rollers */}
    <line x1="50" y1="145" x2="150" y2="145" stroke="currentColor" strokeWidth="3" strokeLinecap="round" className="text-text" />
    <circle cx="70" cy="155" r="5" stroke="currentColor" strokeWidth="2" className="text-accent" />
    <circle cx="100" cy="155" r="5" stroke="currentColor" strokeWidth="2" className="text-accent" />
    <circle cx="130" cy="155" r="5" stroke="currentColor" strokeWidth="2" className="text-accent" />
  </svg>
);
