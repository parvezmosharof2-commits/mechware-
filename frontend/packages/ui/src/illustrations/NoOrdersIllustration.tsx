'use client';

import React from 'react';

export const NoOrdersIllustration: React.FC<React.SVGProps<SVGSVGElement>> = ({
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
    {/* Clean automated storage rack */}
    <rect x="40" y="50" width="120" height="100" rx="4" stroke="currentColor" strokeWidth="2" className="text-surface-border" />
    <line x1="40" y1="85" x2="160" y2="85" stroke="currentColor" strokeWidth="1.5" className="text-surface-border" />
    <line x1="40" y1="120" x2="160" y2="120" stroke="currentColor" strokeWidth="1.5" className="text-surface-border" />
    <line x1="100" y1="50" x2="100" y2="150" stroke="currentColor" strokeWidth="1.5" className="text-surface-border" />

    {/* Standby scanning laser */}
    <line x1="30" y1="100" x2="170" y2="100" stroke="currentColor" strokeWidth="1.5" strokeDasharray="4 4" className="text-accent opacity-60" />

    {/* Empty bay highlight */}
    <circle cx="70" cy="102" r="14" stroke="currentColor" strokeWidth="1.5" strokeDasharray="3 3" className="text-accent" />
    <circle cx="70" cy="102" r="2" fill="currentColor" className="text-accent" />

    {/* Robotic arm standby */}
    <path
      d="M100 25V50M90 25H110"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      className="text-text"
    />
  </svg>
);
