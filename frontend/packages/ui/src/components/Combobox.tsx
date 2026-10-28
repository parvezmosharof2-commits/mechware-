'use client';

import React, { useState, useRef, useEffect } from 'react';
import { Check, ChevronsUpDown, Search, X } from 'lucide-react';
import { cn } from '../utils';

export interface ComboboxOption {
  value: string;
  label: string;
  description?: string;
  category?: string;
}

export interface ComboboxProps {
  options: ComboboxOption[];
  value?: string;
  defaultValue?: string;
  onChange?: (value: string) => void;
  placeholder?: string;
  searchPlaceholder?: string;
  emptyText?: string;
  label?: string;
  helperText?: string;
  error?: string;
  disabled?: boolean;
  className?: string;
}

export const Combobox: React.FC<ComboboxProps> = ({
  options,
  value,
  defaultValue = '',
  onChange,
  placeholder = 'Select an option...',
  searchPlaceholder = 'Search options...',
  emptyText = 'No results found.',
  label,
  helperText,
  error,
  disabled = false,
  className,
}) => {
  const [isOpen, setIsOpen] = useState(false);
  const [internalValue, setInternalValue] = useState<string>(value ?? defaultValue);
  const [searchQuery, setSearchQuery] = useState('');
  const [activeIndex, setActiveIndex] = useState(-1);

  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const currentValue = value !== undefined ? value : internalValue;
  const selectedOption = options.find((opt) => opt.value === currentValue);

  const filteredOptions = options.filter(
    (opt) =>
      opt.label.toLowerCase().includes(searchQuery.toLowerCase()) ||
      (opt.description && opt.description.toLowerCase().includes(searchQuery.toLowerCase()))
  );

  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        setIsOpen(false);
        setSearchQuery('');
      }
    }
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  const handleSelect = (optValue: string) => {
    if (value === undefined) {
      setInternalValue(optValue);
    }
    onChange?.(optValue);
    setIsOpen(false);
    setSearchQuery('');
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (disabled) return;

    if (!isOpen) {
      if (e.key === 'Enter' || e.key === 'ArrowDown' || e.key === ' ') {
        e.preventDefault();
        setIsOpen(true);
      }
      return;
    }

    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setActiveIndex((prev) => (prev < filteredOptions.length - 1 ? prev + 1 : 0));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActiveIndex((prev) => (prev > 0 ? prev - 1 : filteredOptions.length - 1));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (activeIndex >= 0 && activeIndex < filteredOptions.length) {
        handleSelect(filteredOptions[activeIndex].value);
      }
    } else if (e.key === 'Escape') {
      e.preventDefault();
      setIsOpen(false);
    }
  };

  return (
    <div className={cn('relative w-full flex flex-col gap-1.5 font-sans', className)} ref={containerRef}>
      {label && <label className="text-sm font-medium text-text">{label}</label>}

      {/* Trigger Button */}
      <button
        type="button"
        disabled={disabled}
        onClick={() => {
          setIsOpen(!isOpen);
          if (!isOpen) setTimeout(() => inputRef.current?.focus(), 50);
        }}
        onKeyDown={handleKeyDown}
        aria-expanded={isOpen}
        aria-haspopup="listbox"
        className={cn(
          'flex h-11 w-full items-center justify-between rounded-md border bg-bg-muted px-3.5 py-2 text-sm text-text transition-colors duration-fast outline-none select-none disabled:cursor-not-allowed disabled:opacity-50 text-left',
          error
            ? 'border-fault focus:ring-2 focus:ring-fault'
            : 'border-surface-border-strong hover:border-surface-border focus:border-accent focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-bg'
        )}
      >
        <span className={cn('truncate', !selectedOption && 'text-text-subtle')}>
          {selectedOption ? selectedOption.label : placeholder}
        </span>
        <div className="flex items-center gap-1 shrink-0 ml-2">
          {selectedOption && !disabled && (
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                handleSelect('');
              }}
              className="text-text-subtle hover:text-text p-0.5 rounded-full"
              aria-label="Clear selection"
            >
              <X className="h-3.5 w-3.5" />
            </button>
          )}
          <ChevronsUpDown className="h-4 w-4 text-text-muted" />
        </div>
      </button>

      {/* Dropdown Panel */}
      {isOpen && (
        <div className="absolute top-full left-0 z-dropdown mt-1 w-full rounded-md border border-surface-border bg-surface shadow-xl p-1.5 animate-in fade-in-50 duration-fast">
          {/* Search Input */}
          <div className="flex items-center px-2 py-1.5 border-b border-surface-border mb-1">
            <Search className="h-4 w-4 text-text-muted mr-2 shrink-0" />
            <input
              ref={inputRef}
              type="text"
              value={searchQuery}
              onChange={(e) => {
                setSearchQuery(e.target.value);
                setActiveIndex(0);
              }}
              onKeyDown={handleKeyDown}
              placeholder={searchPlaceholder}
              className="w-full bg-transparent text-sm text-text placeholder:text-text-subtle outline-none"
            />
          </div>

          {/* Options List */}
          <div className="max-h-60 overflow-y-auto space-y-0.5" role="listbox">
            {filteredOptions.length === 0 ? (
              <div className="py-4 text-center text-xs text-text-muted">{emptyText}</div>
            ) : (
              filteredOptions.map((opt, idx) => {
                const isSelected = opt.value === currentValue;
                const isActive = idx === activeIndex;

                return (
                  <div
                    key={opt.value}
                    role="option"
                    aria-selected={isSelected}
                    onClick={() => handleSelect(opt.value)}
                    onMouseEnter={() => setActiveIndex(idx)}
                    className={cn(
                      'flex items-center justify-between rounded-sm px-2.5 py-2 text-sm cursor-pointer transition-colors duration-fast select-none',
                      isActive || isSelected ? 'bg-bg-muted text-accent' : 'text-text hover:bg-bg-muted'
                    )}
                  >
                    <div className="flex flex-col">
                      <span className="font-medium">{opt.label}</span>
                      {opt.description && (
                        <span className="text-xs text-text-muted">{opt.description}</span>
                      )}
                    </div>
                    {isSelected && <Check className="h-4 w-4 text-accent shrink-0 ml-2" />}
                  </div>
                );
              })
            )}
          </div>
        </div>
      )}

      {error ? (
        <p className="text-xs text-fault font-medium">{error}</p>
      ) : helperText ? (
        <p className="text-xs text-text-muted">{helperText}</p>
      ) : null}
    </div>
  );
};
