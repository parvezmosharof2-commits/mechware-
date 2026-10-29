'use client';

import React from 'react';
import { AlertCircle, CheckCircle2, Info, AlertTriangle, X } from 'lucide-react';
import { cn } from '../utils';

export type AlertBannerVariant = 'info' | 'success' | 'warning' | 'fault';

export interface AlertBannerProps extends React.HTMLAttributes<HTMLDivElement> {
  variant?: AlertBannerVariant;
  title?: string;
  onDismiss?: () => void;
  action?: React.ReactNode;
}

const variantStyles: Record<
  AlertBannerVariant,
  { container: string; icon: React.ReactNode; text: string }
> = {
  info: {
    container: 'bg-accent/10 border-accent/30 text-text',
    icon: <Info className="h-5 w-5 text-accent shrink-0 mt-0.5" />,
    text: 'text-accent',
  },
  success: {
    container: 'bg-status-online/10 border-status-online/30 text-text',
    icon: <CheckCircle2 className="h-5 w-5 text-status-online shrink-0 mt-0.5" />,
    text: 'text-status-online',
  },
  warning: {
    container: 'bg-status-pending/10 border-status-pending/30 text-text',
    icon: <AlertTriangle className="h-5 w-5 text-status-pending shrink-0 mt-0.5" />,
    text: 'text-status-pending',
  },
  fault: {
    container: 'bg-fault/10 border-fault/30 text-text',
    icon: <AlertCircle className="h-5 w-5 text-fault shrink-0 mt-0.5" />,
    text: 'text-fault',
  },
};

export const AlertBanner: React.FC<AlertBannerProps> = ({
  variant = 'info',
  title,
  children,
  onDismiss,
  action,
  className,
  ...props
}) => {
  const current = variantStyles[variant];

  return (
    <div
      role="alert"
      className={cn(
        'relative flex items-start gap-3 rounded-lg border p-4 font-sans text-sm transition-all',
        current.container,
        className
      )}
      {...props}
    >
      {current.icon}

      <div className="flex-1 flex flex-col gap-1">
        {title && <div className={cn('font-semibold leading-tight', current.text)}>{title}</div>}
        <div className="text-text-muted leading-relaxed text-xs sm:text-sm">{children}</div>
        {action && <div className="mt-2">{action}</div>}
      </div>

      {onDismiss && (
        <button
          type="button"
          onClick={onDismiss}
          className="text-text-subtle hover:text-text p-1 rounded-sm transition-colors"
          aria-label="Dismiss alert"
        >
          <X className="h-4 w-4" />
        </button>
      )}
    </div>
  );
};
