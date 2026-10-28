'use client';

import React, { createContext, useContext, useState, useCallback } from 'react';
import { CheckCircle2, AlertCircle, Info, AlertTriangle, X } from 'lucide-react';
import { cn } from '../utils';

export type ToastVariant = 'info' | 'success' | 'warning' | 'fault';

export interface ToastMessage {
  id: string;
  title: string;
  description?: string;
  variant?: ToastVariant;
  duration?: number;
  action?: {
    label: string;
    onClick: () => void;
  };
}

interface ToastContextType {
  toast: (message: Omit<ToastMessage, 'id'>) => string;
  dismiss: (id: string) => void;
}

const ToastContext = createContext<ToastContextType | undefined>(undefined);

const iconMap: Record<ToastVariant, React.ReactNode> = {
  info: <Info className="h-5 w-5 text-accent shrink-0" />,
  success: <CheckCircle2 className="h-5 w-5 text-status-online shrink-0" />,
  warning: <AlertTriangle className="h-5 w-5 text-status-pending shrink-0" />,
  fault: <AlertCircle className="h-5 w-5 text-status-fault shrink-0" />,
};

const borderMap: Record<ToastVariant, string> = {
  info: 'border-accent/40 shadow-glow-cyan-sm',
  success: 'border-status-online/40',
  warning: 'border-status-pending/40',
  fault: 'border-status-fault/40 shadow-glow-fault',
};

export const ToastProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [toasts, setToasts] = useState<ToastMessage[]>([]);

  const dismiss = useCallback((id: string) => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  const toast = useCallback(
    ({ title, description, variant = 'info', duration = 4000, action }: Omit<ToastMessage, 'id'>) => {
      const id = Math.random().toString(36).substring(2, 9);
      const newToast: ToastMessage = { id, title, description, variant, duration, action };
      setToasts((prev) => [...prev, newToast]);

      if (duration > 0) {
        setTimeout(() => {
          dismiss(id);
        }, duration);
      }
      return id;
    },
    [dismiss]
  );

  return (
    <ToastContext.Provider value={{ toast, dismiss }}>
      {children}
      {/* Toast Viewport */}
      <div
        className="fixed bottom-4 right-4 z-toast flex flex-col gap-2.5 max-w-sm w-full pointer-events-none"
        aria-live="polite"
      >
        {toasts.map((t) => (
          <div
            key={t.id}
            role="status"
            className={cn(
              'pointer-events-auto flex items-start gap-3 rounded-lg border bg-surface p-4 shadow-xl transition-all duration-base font-sans animate-in slide-in-from-bottom-5',
              borderMap[t.variant || 'info']
            )}
          >
            {iconMap[t.variant || 'info']}

            <div className="flex-1 flex flex-col gap-1">
              <h4 className="text-sm font-semibold text-text leading-tight">{t.title}</h4>
              {t.description && (
                <p className="text-xs text-text-muted leading-relaxed">{t.description}</p>
              )}
              {t.action && (
                <button
                  type="button"
                  onClick={() => {
                    t.action?.onClick();
                    dismiss(t.id);
                  }}
                  className="mt-1 text-xs font-bold text-accent hover:underline text-left cursor-pointer"
                >
                  {t.action.label}
                </button>
              )}
            </div>

            <button
              type="button"
              onClick={() => dismiss(t.id)}
              className="text-text-subtle hover:text-text p-0.5 rounded-sm transition-colors"
              aria-label="Close notification"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
};

export function useToast() {
  const context = useContext(ToastContext);
  if (!context) {
    throw new Error('useToast must be used within a ToastProvider');
  }
  return context;
}
