'use client';

import React from 'react';

export const EmptyCartIllustration: React.FC<React.SVGProps<SVGSVGElement>> = ({
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
    {/* Circuit grid background */}
    <path
      d="M20 100H40M160 100H180M100 20V40M100 160V180"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeDasharray="4 4"
      className="text-surface-border opacity-50"
    />
    <circle cx="40" cy="100" r="3" fill="currentColor" className="text-surface-border" />
    <circle cx="160" cy="100" r="3" fill="currentColor" className="text-surface-border" />

    {/* Industrial Cart / Heavy duty Container */}
    <rect
      x="55"
      y="70"
      width="90"
      height="60"
      rx="6"
      stroke="currentColor"
      strokeWidth="2"
      className="text-text"
    />
    {/* Reinforcing industrial ribs */}
    <line x1="85" y1="70" x2="85" y2="130" stroke="currentColor" strokeWidth="1.5" className="text-surface-border" />
    <line x1="115" y1="70" x2="115" y2="130" stroke="currentColor" strokeWidth="1.5" className="text-surface-border" />

    {/* Cart Handle */}
    <path
      d="M55 85H40V60"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="text-text"
    />

    {/* Cybernetic Accent Circuit Lines inside Container */}
    <path
      d="M70 100H130M100 85V115"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      className="text-accent"
    />
    <circle cx="100" cy="100" r="5" stroke="currentColor" strokeWidth="2" className="text-accent" />
    <circle cx="100" cy="100" r="2" fill="currentColor" className="text-accent" />

    {/* Industrial Wheels */}
    <circle cx="75" cy="145" r="9" stroke="currentColor" strokeWidth="2" className="text-text" />
    <circle cx="75" cy="145" r="3" fill="currentColor" className="text-accent" />

    <circle cx="125" cy="145" r="9" stroke="currentColor" strokeWidth="2" className="text-text" />
    <circle cx="125" cy="145" r="3" fill="currentColor" className="text-accent" />
  </svg>
);
