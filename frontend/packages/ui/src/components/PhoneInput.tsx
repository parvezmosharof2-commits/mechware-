'use client';

import React, { useState } from 'react';
import { AlertCircle, CheckCircle2 } from 'lucide-react';
import { cn } from '../utils';

export interface PhoneInputProps {
  label?: string;
  helperText?: string;
  error?: string;
  value?: string;
  defaultValue?: string;
  onChange?: (fullPhone: string, isValid: boolean) => void;
  disabled?: boolean;
  required?: boolean;
  className?: string;
}

const countryCodes = [
  { code: '+880', flag: '🇧🇩', name: 'Bangladesh', placeholder: '01712345678' },
  { code: '+1', flag: '🇺🇸', name: 'United States', placeholder: '2025550143' },
  { code: '+44', flag: '🇬🇧', name: 'United Kingdom', placeholder: '7911123456' },
  { code: '+49', flag: '🇩🇪', name: 'Germany', placeholder: '15123456789' },
  { code: '+81', flag: '🇯🇵', name: 'Japan', placeholder: '9012345678' },
];

export const PhoneInput: React.FC<PhoneInputProps> = ({
  label = 'Phone Number',
  helperText = 'Bangladesh mobile number format: 01XXXXXXXXX',
  error: customError,
  value,
  defaultValue = '',
  onChange,
  disabled = false,
  required = false,
  className,
}) => {
  const [selectedCountry, setSelectedCountry] = useState(countryCodes[0]);
  const [rawNumber, setRawNumber] = useState(value ?? defaultValue);
  const [touched, setTouched] = useState(false);

  const validateBdPhone = (phone: string): boolean => {
    // 013, 014, 015, 016, 017, 018, 019 followed by 8 digits
    const cleaned = phone.replace(/\D/g, '');
    return /^01[3-9]\d{8}$/.test(cleaned);
  };

  const isBd = selectedCountry.code === '+880';
  const isValid = isBd ? validateBdPhone(rawNumber) : rawNumber.replace(/\D/g, '').length >= 7;
  const showError = touched && rawNumber.length > 0 && !isValid;

  const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const val = e.target.value;
    setRawNumber(val);
    const valid = isBd ? validateBdPhone(val) : val.replace(/\D/g, '').length >= 7;
    onChange?.(`${selectedCountry.code}${val}`, valid);
  };

  return (
    <div className={cn('w-full flex flex-col gap-1.5 font-sans', className)}>
      {label && (
        <label className="text-sm font-medium text-text">
          {label}
          {required && <span className="text-fault ml-1">*</span>}
        </label>
      )}

      <div
        className={cn(
          'relative flex items-center w-full min-h-[44px] rounded-md border bg-bg-muted transition-colors duration-fast overflow-hidden',
          customError || showError
            ? 'border-fault focus-within:ring-2 focus-within:ring-fault'
            : isValid && rawNumber.length > 0
            ? 'border-status-online focus-within:ring-2 focus-within:ring-status-online'
            : 'border-surface-border-strong focus-within:border-accent focus-within:ring-2 focus-within:ring-accent focus-within:ring-offset-2 focus-within:ring-offset-bg',
          disabled && 'opacity-50 cursor-not-allowed bg-bg-subtle'
        )}
      >
        {/* Country Code Select */}
        <div className="flex items-center pl-3 pr-2 border-r border-surface-border bg-bg-subtle/50 shrink-0">
          <select
            value={selectedCountry.code}
            onChange={(e) => {
              const found = countryCodes.find((c) => c.code === e.target.value) || countryCodes[0];
              setSelectedCountry(found);
            }}
            disabled={disabled}
            className="bg-transparent text-sm text-text font-medium outline-none cursor-pointer pr-1"
          >
            {countryCodes.map((c) => (
              <option key={c.code} value={c.code} className="bg-surface text-text">
                {c.flag} {c.code}
              </option>
            ))}
          </select>
        </div>

        {/* Input */}
        <input
          type="tel"
          disabled={disabled}
          value={rawNumber}
          onChange={handleInputChange}
          onBlur={() => setTouched(true)}
          placeholder={selectedCountry.placeholder}
          className="w-full bg-transparent px-3.5 py-2.5 text-text text-sm placeholder:text-text-subtle outline-none border-none disabled:cursor-not-allowed tabular-nums"
        />

        {/* Status Icon */}
        <div className="pr-3.5 flex items-center shrink-0">
          {isValid && rawNumber.length > 0 ? (
            <CheckCircle2 className="h-4 w-4 text-status-online" />
          ) : showError ? (
            <AlertCircle className="h-4 w-4 text-fault" />
          ) : null}
        </div>
      </div>

      {customError || showError ? (
        <p className="flex items-center gap-1.5 text-xs text-fault font-medium" role="alert">
          <AlertCircle className="h-3.5 w-3.5 shrink-0" />
          <span>
            {customError ||
              (isBd
                ? 'Invalid Bangladesh phone number. Format: 01712345678 (11 digits)'
                : 'Invalid phone number.')}
          </span>
        </p>
      ) : helperText ? (
        <p className="text-xs text-text-muted">{helperText}</p>
      ) : null}
    </div>
  );
};
