'use client';

import React, { useState, useEffect, useRef } from 'react';
import { Search, Command, ArrowRight, CornerDownLeft } from 'lucide-react';
import { cn } from '../utils';

export interface CommandItem {
  id: string;
  title: string;
  group: string;
  icon?: React.ReactNode;
  shortcut?: string;
  onSelect: () => void;
}

export interface CommandPaletteProps {
  items: CommandItem[];
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  placeholder?: string;
}

export const CommandPalette: React.FC<CommandPaletteProps> = ({
  items,
  open: externalOpen,
  onOpenChange,
  placeholder = 'Type a command or search industrial modules...',
}) => {
  const [internalOpen, setInternalOpen] = useState(false);
  const isOpen = externalOpen !== undefined ? externalOpen : internalOpen;
  const setOpen = onOpenChange || setInternalOpen;

  const [search, setSearch] = useState('');
  const [selectedIndex, setSelectedIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  // Listen for Cmd+K / Ctrl+K
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
        e.preventDefault();
        setOpen(!isOpen);
      } else if (e.key === 'Escape' && isOpen) {
        e.preventDefault();
        setOpen(false);
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isOpen, setOpen]);

  // Focus input when opened
  useEffect(() => {
    if (isOpen) {
      setTimeout(() => inputRef.current?.focus(), 50);
      setSelectedIndex(0);
    } else {
      setSearch('');
    }
  }, [isOpen]);

  const filteredItems = items.filter(
    (item) =>
      item.title.toLowerCase().includes(search.toLowerCase()) ||
      item.group.toLowerCase().includes(search.toLowerCase())
  );

  // Group filtered items
  const grouped = filteredItems.reduce<Record<string, CommandItem[]>>((acc, item) => {
    if (!acc[item.group]) acc[item.group] = [];
    acc[item.group].push(item);
    return acc;
  }, {});

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setSelectedIndex((prev) => (prev < filteredItems.length - 1 ? prev + 1 : 0));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setSelectedIndex((prev) => (prev > 0 ? prev - 1 : filteredItems.length - 1));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (filteredItems[selectedIndex]) {
        filteredItems[selectedIndex].onSelect();
        setOpen(false);
      }
    }
  };

  if (!isOpen) return null;

  let flatIndexCounter = -1;

  return (
    <div
      className="fixed inset-0 z-modal bg-black/80 backdrop-blur-sm flex items-start justify-center pt-20 px-4 animate-in fade-in-50 duration-fast select-none font-sans"
      onClick={() => setOpen(false)}
    >
      <div
        className="w-full max-w-xl rounded-xl border border-surface-border bg-surface shadow-2xl overflow-hidden animate-in zoom-in-95 duration-fast"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Search Header */}
        <div className="flex items-center px-4 py-3 border-b border-surface-border gap-3">
          <Search className="h-5 w-5 text-accent shrink-0" />
          <input
            ref={inputRef}
            type="text"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setSelectedIndex(0);
            }}
            onKeyDown={handleKeyDown}
            placeholder={placeholder}
            className="w-full bg-transparent text-text text-base placeholder:text-text-subtle outline-none border-none"
          />
          <div className="flex items-center gap-1 text-[11px] font-mono px-2 py-0.5 rounded-sm bg-bg-muted border border-surface-border text-text-subtle">
            <Command className="h-3 w-3" />
            <span>K</span>
          </div>
        </div>

        {/* Results List */}
        <div className="max-h-80 overflow-y-auto p-2 space-y-3" role="listbox">
          {filteredItems.length === 0 ? (
            <div className="py-8 text-center text-sm text-text-muted">
              No matching commands or industrial components.
            </div>
          ) : (
            Object.entries(grouped).map(([group, groupItems]) => (
              <div key={group} className="space-y-1">
                <span className="px-3 text-[11px] font-semibold uppercase tracking-wider text-text-subtle">
                  {group}
                </span>
                {groupItems.map((item) => {
                  flatIndexCounter++;
                  const isCurrent = flatIndexCounter === selectedIndex;
                  return (
                    <div
                      key={item.id}
                      role="option"
                      aria-selected={isCurrent}
                      onClick={() => {
                        item.onSelect();
                        setOpen(false);
                      }}
                      onMouseEnter={() => setSelectedIndex(flatIndexCounter)}
                      className={cn(
                        'flex items-center justify-between rounded-md px-3 py-2.5 text-sm cursor-pointer transition-colors duration-fast',
                        isCurrent
                          ? 'bg-accent/15 text-accent border border-accent/30'
                          : 'text-text hover:bg-bg-muted'
                      )}
                    >
                      <div className="flex items-center gap-2.5">
                        {item.icon && (
                          <div className={cn('shrink-0', isCurrent ? 'text-accent' : 'text-text-muted')}>
                            {item.icon}
                          </div>
                        )}
                        <span className="font-medium">{item.title}</span>
                      </div>

                      <div className="flex items-center gap-2">
                        {item.shortcut && (
                          <span className="text-[11px] font-mono text-text-subtle px-1.5 py-0.5 rounded-xs bg-bg-muted border border-surface-border">
                            {item.shortcut}
                          </span>
                        )}
                        {isCurrent && <CornerDownLeft className="h-3.5 w-3.5 text-accent" />}
                      </div>
                    </div>
                  );
                })}
              </div>
            ))
          )}
        </div>

        {/* Footer info bar */}
        <div className="flex items-center justify-between px-4 py-2 border-t border-surface-border bg-bg-muted text-[11px] text-text-subtle">
          <span>Navigate with ↑ and ↓</span>
          <span>Press Enter to execute</span>
        </div>
      </div>
    </div>
  );
};
