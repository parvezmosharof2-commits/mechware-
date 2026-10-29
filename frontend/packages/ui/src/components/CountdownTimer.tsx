'use client';

import React, { useState, useEffect } from 'react';
import { Clock } from 'lucide-react';
import { cn } from '../utils';

export interface CountdownTimerProps {
  targetDate: Date | string | number;
  label?: string;
  size?: 'sm' | 'md' | 'lg';
  onExpire?: () => void;
  className?: string;
}

interface TimeRemaining {
  days: number;
  hours: number;
  minutes: number;
  seconds: number;
  isExpired: boolean;
}

function calculateTimeRemaining(target: number): TimeRemaining {
  const now = Date.now();
  const diff = target - now;

  if (diff <= 0) {
    return { days: 0, hours: 0, minutes: 0, seconds: 0, isExpired: true };
  }

  const days = Math.floor(diff / (1000 * 60 * 60 * 24));
  const hours = Math.floor((diff / (1000 * 60 * 60)) % 24);
  const minutes = Math.floor((diff / (1000 * 60)) % 60);
  const seconds = Math.floor((diff / 1000) % 60);

  return { days, hours, minutes, seconds, isExpired: false };
}

export const CountdownTimer: React.FC<CountdownTimerProps> = ({
  targetDate,
  label = 'Deal ends in',
  size = 'md',
  onExpire,
  className,
}) => {
  const targetMs = React.useMemo(() => new Date(targetDate).getTime(), [targetDate]);
  const [time, setTime] = useState<TimeRemaining>(() => calculateTimeRemaining(targetMs));

  useEffect(() => {
    const timer = setInterval(() => {
      const remaining = calculateTimeRemaining(targetMs);
      setTime(remaining);
      if (remaining.isExpired) {
        clearInterval(timer);
        onExpire?.();
      }
    }, 1000);
    (timer as any).unref?.();

    return () => clearInterval(timer);
  }, [targetMs, onExpire]);

  const padZero = (n: number) => n.toString().padStart(2, '0');

  const sizeBox = {
    sm: 'px-1.5 py-0.5 text-xs rounded-xs min-w-[28px]',
    md: 'px-2 py-1 text-sm rounded-sm min-w-[34px]',
    lg: 'px-3 py-1.5 text-lg rounded-md min-w-[44px]',
  };

  return (
    <div
      className={cn('inline-flex items-center gap-2 font-sans select-none', className)}
      role="timer"
      aria-label={`${label}: ${time.days}d ${time.hours}h ${time.minutes}m ${time.seconds}s`}
    >
      {label && (
        <span className="flex items-center gap-1.5 text-xs font-semibold text-text-muted uppercase tracking-wider">
          <Clock className="h-3.5 w-3.5 text-fault animate-pulse" />
          <span>{label}</span>
        </span>
      )}

      <div className="flex items-center gap-1 text-center font-mono">
        {time.days > 0 && (
          <>
            <div className={cn('bg-bg-muted border border-surface-border font-bold text-text tabular-nums', sizeBox[size])}>
              {padZero(time.days)}
              <span className="block text-[9px] font-sans font-normal text-text-subtle">d</span>
            </div>
            <span className="text-text-subtle font-bold">:</span>
          </>
        )}

        <div className={cn('bg-bg-muted border border-surface-border font-bold text-text tabular-nums', sizeBox[size])}>
          {padZero(time.hours)}
          <span className="block text-[9px] font-sans font-normal text-text-subtle">h</span>
        </div>
        <span className="text-text-subtle font-bold">:</span>

        <div className={cn('bg-bg-muted border border-surface-border font-bold text-text tabular-nums', sizeBox[size])}>
          {padZero(time.minutes)}
          <span className="block text-[9px] font-sans font-normal text-text-subtle">m</span>
        </div>
        <span className="text-text-subtle font-bold">:</span>

        <div className={cn('bg-fault/15 border border-fault/30 font-bold text-fault tabular-nums', sizeBox[size])}>
          {padZero(time.seconds)}
          <span className="block text-[9px] font-sans font-normal text-fault/70">s</span>
        </div>
      </div>
    </div>
  );
};
