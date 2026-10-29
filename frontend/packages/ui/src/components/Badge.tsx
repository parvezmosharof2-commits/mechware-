'use client';

import React from 'react';
import { Sparkles, ShieldCheck, Flame, Tag, Truck, AlertTriangle } from 'lucide-react';
import { cn } from '../utils';

export interface BadgeProps extends React.HTMLAttributes<HTMLSpanElement> {
  variant?:
    | 'default'
    | 'deal'
    | 'new'
    | 'bestseller'
    | 'authentic'
    | 'low-stock'
    | 'free-delivery'
    | 'ai'
    | 'outline'
    | 'success';
  size?: 'sm' | 'md';
  pulse?: boolean;
}

const variantConfig: Record<
  NonNullable<BadgeProps['variant']>,
  { className: string; icon?: React.ReactNode }
> = {
  default: {
    className: 'bg-bg-muted text-text-muted border-surface-border',
  },
  deal: {
    className: 'bg-fault/15 text-fault border-fault/30 font-bold',
    icon: <Tag className="h-3 w-3 shrink-0" />,
  },
  new: {
    className: 'bg-accent/15 text-accent border-accent/40 font-semibold',
  },
  bestseller: {
    className: 'bg-warning/15 text-warning border-warning/30 font-semibold',
    icon: <Flame className="h-3 w-3 shrink-0 text-warning" />,
  },
  authentic: {
    className: 'bg-brand-navy/15 text-accent border-accent/40 font-medium',
    icon: <ShieldCheck className="h-3 w-3 shrink-0 text-accent" />,
  },
  'low-stock': {
    className: 'bg-warning/15 text-warning border-warning/30 font-medium',
    icon: <AlertTriangle className="h-3 w-3 shrink-0" />,
  },
  'free-delivery': {
    className: 'bg-status-online/15 text-status-online border-status-online/30 font-medium',
    icon: <Truck className="h-3 w-3 shrink-0" />,
  },
  ai: {
    className:
      'bg-accent/15 text-accent border-accent/40 font-semibold shadow-glow-cyan-sm',
    icon: <Sparkles className="h-3 w-3 shrink-0 text-accent" />,
  },
  outline: {
    className: 'bg-transparent text-text-muted border-surface-border',
  },
  success: {
    className: 'bg-status-online/15 text-status-online border-status-online/30 font-medium',
  },
};

const sizeStyles = {
  sm: 'text-[11px] px-2 py-0.5 gap-1 rounded-xs',
  md: 'text-xs px-2.5 py-1 gap-1.5 rounded-sm',
};

export const Badge: React.FC<BadgeProps> = ({
  variant = 'default',
  size = 'md',
  pulse = false,
  className,
  children,
  ...props
}) => {
  const config = variantConfig[variant];

  return (
    <span
      className={cn(
        'inline-flex items-center border font-sans select-none tracking-wide transition-colors',
        config.className,
        sizeStyles[size],
        className
      )}
      {...props}
    >
      {pulse && (
        <span className="relative flex h-2 w-2 mr-0.5">
          <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-current opacity-75" />
          <span className="relative inline-flex rounded-full h-2 w-2 bg-current" />
        </span>
      )}
      {config.icon}
      <span>{children}</span>
    </span>
  );
};
