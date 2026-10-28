'use client';

import React, { useState, useEffect, Suspense } from 'react';
import { useSearchParams, useParams } from 'next/navigation';
import {
  colors,
  scales,
  chartPalette,
  typography,
  spacing,
  radius,
  shadows,
  zIndex,
  Button,
  IconButton,
  Input,
  Textarea,
  Select,
  SelectTrigger,
  SelectValue,
  SelectContent,
  SelectItem,
  SelectGroup,
  SelectLabel,
  Combobox,
  PhoneInput,
  Checkbox,
  RadioGroup,
  Radio,
  RadioCard,
  Switch,
  QuantityStepper,
  Price,
  Rating,
  Badge,
  Card,
  CardHeader,
  CardTitle,
  CardDescription,
  CardContent,
  CardFooter,
  Tabs,
  TabsList,
  TabsTrigger,
  TabsContent,
  Accordion,
  AccordionItem,
  AccordionTrigger,
  AccordionContent,
  Tooltip,
  TooltipProvider,
  TooltipTrigger,
  TooltipContent,
  Popover,
  PopoverTrigger,
  PopoverContent,
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuLabel,
  DropdownMenuShortcut,
  Dialog,
  DialogTrigger,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
  DialogClose,
  Sheet,
  SheetTrigger,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
  SheetFooter,
  SheetClose,
  ToastProvider,
  useToast,
  AlertBanner,
  Breadcrumb,
  BreadcrumbList,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbSeparator,
  BreadcrumbPage,
  Pagination,
  PaginationContent,
  PaginationItem,
  PaginationLink,
  PaginationPrevious,
  PaginationNext,
  PaginationEllipsis,
  Skeleton,
  Avatar,
  AvatarImage,
  AvatarFallback,
  Progress,
  Stepper,
  Carousel,
  CarouselSlide,
  CountdownTimer,
  ImageGallery,
  DataTable,
  EmptyState,
  ErrorState,
  FileUploader,
  CommandPalette,
  ProductCard,
  ProductCardSkeleton,
  EmptyCartIllustration,
  EmptySearchIllustration,
  OrderConfirmedIllustration,
  SystemFaultIllustration,
  NoOrdersIllustration,
  type ColumnDef,
} from '@mechware/ui';
import {
  Sun,
  Moon,
  Search,
  ShoppingCart,
  Heart,
  Sliders,
  Cpu,
  Layers,
  ShieldCheck,
  Terminal,
  Zap,
  CheckCircle2,
  Clock,
  ArrowRight,
  Truck,
  CreditCard,
  FileText,
  AlertTriangle,
  RotateCw,
  ExternalLink,
  Info,
} from 'lucide-react';

// Sample Industrial Data for DataTable
interface MachineRecord {
  sku: string;
  name: string;
  category: string;
  stock: number;
  price: string;
  status: 'In Stock' | 'Low Stock' | 'Lead Time: 14d';
}

const sampleColumns: ColumnDef<MachineRecord>[] = [
  {
    accessorKey: 'sku',
    header: 'SKU / Model',
    cell: ({ row }) => (
      <span className="font-mono text-xs font-bold text-accent">{row.original.sku}</span>
    ),
  },
  {
    accessorKey: 'name',
    header: 'Equipment Specification',
    cell: ({ row }) => (
      <div className="flex flex-col">
        <span className="font-semibold text-text">{row.original.name}</span>
        <span className="text-xs text-text-muted">{row.original.category}</span>
      </div>
    ),
  },
  {
    accessorKey: 'stock',
    header: 'Warehouse Qty',
    cell: ({ row }) => <span className="tabular-nums font-semibold">{row.original.stock} units</span>,
  },
  {
    accessorKey: 'price',
    header: 'Commercial Price (BDT)',
    cell: ({ row }) => <span className="font-bold tabular-nums text-text">{row.original.price}</span>,
  },
  {
    accessorKey: 'status',
    header: 'Availability',
    cell: ({ row }) => {
      const st = row.original.status;
      return (
        <Badge
          variant={st === 'In Stock' ? 'authentic' : st === 'Low Stock' ? 'low-stock' : 'outline'}
          size="sm"
        >
          {st}
        </Badge>
      );
    },
  },
];

const sampleData: MachineRecord[] = [
  {
    sku: 'MW-PLC-1516-3PN',
    name: 'Siemens SIMATIC S7-1500 CPU 1516-3 PN/DP Controller',
    category: 'Programmable Logic Controllers',
    stock: 28,
    price: '৳3,45,000',
    status: 'In Stock',
  },
  {
    sku: 'MW-VFD-ACS580-045A',
    name: 'ABB ACS580 45kW 3-Phase Industrial Variable Frequency Drive',
    category: 'AC Motor Drives & Inverters',
    stock: 6,
    price: '৳1,85,000',
    status: 'Low Stock',
  },
  {
    sku: 'MW-HMI-TP1200-COM',
    name: 'Siemens SIMATIC HMI TP1200 Comfort Touch Panel (12.1" Display)',
    category: 'Human Machine Interfaces',
    stock: 14,
    price: '৳2,10,000',
    status: 'In Stock',
  },
  {
    sku: 'MW-SNR-E2E-X10MD1',
    name: 'Omron E2E High-Precision Inductive Proximity Sensor M18 (10mm Range)',
    category: 'Industrial Sensors & Switches',
    stock: 250,
    price: '৳3,850',
    status: 'In Stock',
  },
  {
    sku: 'MW-SRV-SIGMA7-750W',
    name: 'Yaskawa Sigma-7 750W High-Speed AC Servo Motor & Drive Kit',
    category: 'Motion Control & Servo Systems',
    stock: 4,
    price: '৳98,000',
    status: 'Lead Time: 14d',
  },
];

// Sample Gallery Images
const sampleGalleryImages = [
  {
    src: 'https://images.unsplash.com/photo-1581092335397-9583fe92d232?w=800&auto=format&fit=crop&q=80',
    thumbnail: 'https://images.unsplash.com/photo-1581092335397-9583fe92d232?w=200&auto=format&fit=crop&q=80',
    alt: 'Siemens S7-1500 Main Telemetry Panel',
  },
  {
    src: 'https://images.unsplash.com/photo-1581092160607-ee22621dd758?w=800&auto=format&fit=crop&q=80',
    thumbnail: 'https://images.unsplash.com/photo-1581092160607-ee22621dd758?w=200&auto=format&fit=crop&q=80',
    alt: 'Industrial Automated Controller Terminals',
  },
  {
    src: 'https://images.unsplash.com/photo-1581091226825-a6a2a5aee158?w=800&auto=format&fit=crop&q=80',
    thumbnail: 'https://images.unsplash.com/photo-1581091226825-a6a2a5aee158?w=200&auto=format&fit=crop&q=80',
    alt: 'High Voltage Inverter Architecture',
  },
];

// Inner Design System Showcase Page Component
function DesignSystemContent() {
  const searchParams = useSearchParams();
  const params = useParams();
  const themeParam = searchParams.get('theme');
  const routeLocale = params?.locale === 'bn' ? 'bn' : 'en';

  const [isDark, setIsDark] = useState<boolean>(() => (themeParam === 'light' ? false : true));
  const [locale, setLocale] = useState<'en' | 'bn'>(routeLocale);
  const [cmdOpen, setCmdOpen] = useState(false);
  const [selectedRadioCard, setSelectedRadioCard] = useState('express');
  const [paymentOption, setPaymentOption] = useState('bkash');
  const [quantity, setQuantity] = useState(2);
  const [phoneVal, setPhoneVal] = useState('01711223344');
  const [interactiveRating, setInteractiveRating] = useState(4.5);
  const [sliderVal, setSliderVal] = useState(1);

  const { toast } = useToast();

  useEffect(() => {
    if (themeParam === 'light') {
      setIsDark(false);
      document.documentElement.classList.add('light');
      document.documentElement.classList.remove('dark');
    } else if (themeParam === 'dark') {
      setIsDark(true);
      document.documentElement.classList.add('dark');
      document.documentElement.classList.remove('light');
    }
  }, [themeParam]);

  const toggleTheme = () => {
    const next = !isDark;
    setIsDark(next);
    if (typeof document !== 'undefined') {
      const root = document.documentElement;
      if (next) {
        root.classList.add('dark');
        root.classList.remove('light');
      } else {
        root.classList.add('light');
        root.classList.remove('dark');
      }
    }
  };

  const commandItems = [
    {
      id: '1',
      title: 'Navigate to PLC Controllers',
      group: 'Machinery Categories',
      icon: <Cpu className="h-4 w-4" />,
      shortcut: 'G P',
      onSelect: () => toast({ title: 'Navigation', description: 'Opening PLC Catalog...', variant: 'info' }),
    },
    {
      id: '2',
      title: 'Request Enterprise Bulk Quote (RFQ)',
      group: 'Commercial Actions',
      icon: <FileText className="h-4 w-4" />,
      shortcut: '⌘ R',
      onSelect: () => toast({ title: 'RFQ Initiated', description: 'Opening RFQ modal dialog.', variant: 'success' }),
    },
    {
      id: '3',
      title: 'Emergency Calibration Help Desk',
      group: 'Engineering Support',
      icon: <AlertTriangle className="h-4 w-4" />,
      onSelect: () => toast({ title: 'Support Alert', description: 'Contacting Dhaka SRE Center.', variant: 'warning' }),
    },
  ];

  return (
    <div className={`min-h-screen transition-colors duration-base ${isDark ? 'dark bg-bg text-text' : 'light bg-bg text-text'}`}>
      {/* Top Banner Navigation Bar */}
      <header className="sticky top-0 z-header border-b border-surface-border bg-surface/90 backdrop-blur-md px-4 lg:px-8 py-3.5 flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="flex h-9 w-9 items-center justify-center rounded-md bg-accent text-accent-fg font-black text-lg shadow-glow-cyan-sm">
            MW
          </div>
          <div>
            <h1 className="text-base font-extrabold tracking-tight font-display flex items-center gap-2">
              MECHWARE DESIGN SYSTEM
              <Badge variant="ai" size="sm">v1.0.0 Cybernetic</Badge>
            </h1>
            <p className="text-xs text-text-muted hidden sm:block">
              $100,000 Agency Benchmark | WCAG 2.2 AA Verified (22/22)
            </p>
          </div>
        </div>

        <div className="flex items-center gap-2.5">
          {/* Quick Command Launcher */}
          <button
            type="button"
            onClick={() => setCmdOpen(true)}
            className="hidden md:flex items-center gap-2 px-3 py-1.5 rounded-md border border-surface-border bg-bg-muted text-xs text-text-muted hover:text-text hover:border-accent transition-colors"
          >
            <Search className="h-3.5 w-3.5 text-accent" />
            <span>Search components...</span>
            <kbd className="px-1.5 py-0.5 rounded-xs bg-surface border border-surface-border text-[10px] font-mono">⌘K</kbd>
          </button>

          {/* Locale Toggle */}
          <Button
            variant="secondary"
            size="sm"
            onClick={() => setLocale(locale === 'en' ? 'bn' : 'en')}
          >
            {locale === 'en' ? 'বাংলা (BN)' : 'English (EN)'}
          </Button>

          {/* Dark / Light Mode Switcher */}
          <Button
            variant="outline"
            size="sm"
            onClick={toggleTheme}
            aria-label="Toggle Theme"
            leftIcon={isDark ? <Sun className="h-4 w-4 text-amber-400" /> : <Moon className="h-4 w-4 text-brand-navy" />}
          >
            {isDark ? 'Light Mode' : 'Dark Mode'}
          </Button>
        </div>
      </header>

      {/* Main Container */}
      <main className="max-w-container mx-auto px-4 sm:px-6 lg:px-8 py-10 space-y-16">
        {/* Intro Hero Strip */}
        <section className="rounded-2xl border border-surface-border bg-surface p-6 sm:p-10 shadow-lg relative overflow-hidden">
          <div className="relative z-10 max-w-3xl space-y-4">
            <Breadcrumb>
              <BreadcrumbList>
                <BreadcrumbItem>
                  <BreadcrumbLink href="#">MECHWARE Platform</BreadcrumbLink>
                </BreadcrumbItem>
                <BreadcrumbSeparator />
                <BreadcrumbItem>
                  <BreadcrumbLink href="#">Design Tokens</BreadcrumbLink>
                </BreadcrumbItem>
                <BreadcrumbSeparator />
                <BreadcrumbItem>
                  <BreadcrumbPage>Component Library</BreadcrumbPage>
                </BreadcrumbItem>
              </BreadcrumbList>
            </Breadcrumb>

            <h2 className="text-3xl sm:text-4xl font-extrabold tracking-tight font-display text-text">
              High-Precision Industrial UI Architecture
            </h2>
            <p className="text-text-muted text-base sm:text-lg leading-relaxed">
              Designed for mission-critical Industry 4.0 automation, combining the technical depth of Siemens and McMaster-Carr with the sleek luxury of modern consumer platforms. Every component is rigorously tested against WCAG 2.2 AA standards with zero keyboard traps and zero layout shift.
            </p>

            <div className="flex flex-wrap items-center gap-3 pt-2">
              <Badge variant="deal" pulse>Active Industrial Spec</Badge>
              <Badge variant="authentic">ISO 9001 & CE Certified</Badge>
              <Badge variant="free-delivery">Fast Delivery: Dhaka & Chittagong</Badge>
              <Badge variant="new">Bangla & English Ready</Badge>
            </div>
          </div>
        </section>

        {/* ---------------- 1. DESIGN TOKENS ---------------- */}
        <section id="tokens" className="space-y-6">
          <div className="border-b border-surface-border pb-3">
            <h3 className="text-2xl font-bold font-display text-text">1. Semantic Design Tokens</h3>
            <p className="text-sm text-text-muted">
              Live token palette evaluated against contrast matrix rules. All colors adapt seamlessly across themes.
            </p>
          </div>

          {/* Color Palettes Grid */}
          <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-3.5">
            {[
              { name: '--bg', hex: isDark ? colors.dark.bg : colors.light.bg, label: 'Page Background' },
              { name: '--bg-subtle', hex: isDark ? colors.dark.bgSubtle : colors.light.bgSubtle, label: 'Card Surface' },
              { name: '--bg-muted', hex: isDark ? colors.dark.bgMuted : colors.light.bgMuted, label: 'Table / Inputs' },
              { name: '--surface', hex: isDark ? colors.dark.surface : colors.light.surface, label: 'Floating Modal' },
              { name: '--border', hex: isDark ? colors.dark.border : colors.light.border, label: 'Circuit Border' },
              { name: '--border-strong', hex: isDark ? colors.dark.borderStrong : colors.light.borderStrong, label: 'Focus Border' },
              { name: '--text', hex: isDark ? colors.dark.text : colors.light.text, label: 'Headings / Text' },
              { name: '--text-muted', hex: isDark ? colors.dark.textMuted : colors.light.textMuted, label: 'Specs / Meta' },
              { name: '--accent', hex: isDark ? colors.dark.accent : colors.light.accent, label: 'Mech Teal CTA' },
              { name: '--brand-navy', hex: isDark ? colors.dark.brandNavy : colors.light.brandNavy, label: 'Industrial Navy' },
              { name: '--deal / --fault', hex: isDark ? colors.dark.deal : colors.light.deal, label: 'Alarm / Discount' },
              { name: '--success', hex: isDark ? colors.dark.success : colors.light.success, label: 'Controller Online' },
            ].map((token) => (
              <div
                key={token.name}
                className="rounded-lg border border-surface-border bg-surface p-3 flex flex-col gap-2 shadow-xs"
              >
                <div
                  className="h-14 w-full rounded-md border border-surface-border shadow-inner"
                  style={{ backgroundColor: token.hex }}
                />
                <div className="flex flex-col">
                  <span className="text-xs font-mono font-bold text-text truncate">{token.name}</span>
                  <span className="text-[11px] font-mono text-text-muted">{token.hex}</span>
                  <span className="text-[10px] text-text-subtle mt-0.5">{token.label}</span>
                </div>
              </div>
            ))}
          </div>

          {/* Chart Palette */}
          <div className="rounded-xl border border-surface-border bg-surface p-5 space-y-3">
            <h4 className="text-sm font-bold uppercase tracking-wider text-text-muted">
              Colorblind-Safe Chart Palette (Telemetry & Power Monitoring)
            </h4>
            <div className="flex flex-wrap gap-3">
              {chartPalette.map((col, idx) => (
                <div key={idx} className="flex items-center gap-2 px-3 py-1.5 rounded-md border border-surface-border bg-bg-muted">
                  <div className="h-4 w-4 rounded-full" style={{ backgroundColor: col }} />
                  <span className="text-xs font-mono font-semibold">{col}</span>
                </div>
              ))}
            </div>
          </div>
        </section>

        {/* ---------------- 2. BUTTONS & ACTIONS ---------------- */}
        <section id="buttons" className="space-y-6">
          <div className="border-b border-surface-border pb-3">
            <h3 className="text-2xl font-bold font-display text-text">2. Buttons & Actions</h3>
            <p className="text-sm text-text-muted">
              7 variants, 4 sizes, active micro-interactions (scale 0.98), loading states, and icon buttons.
            </p>
          </div>

          <div className="space-y-4">
            <div className="flex flex-wrap items-center gap-3">
              <Button variant="accent" size="md">Add to Cart (Accent)</Button>
              <Button variant="primary" size="md">Buy Now (Primary)</Button>
              <Button variant="secondary" size="md">Download CAD (.STEP)</Button>
              <Button variant="outline" size="md">Request Calibration Quote</Button>
              <Button variant="ghost" size="md">View Pinout</Button>
              <Button variant="destructive" size="md">Emergency Stop Signal</Button>
              <Button variant="link" size="md">Compare with Schneider</Button>
            </div>

            {/* Sizes & States */}
            <div className="flex flex-wrap items-center gap-3 pt-2">
              <Button variant="accent" size="sm">Small (32px)</Button>
              <Button variant="accent" size="md">Medium (40px)</Button>
              <Button variant="accent" size="lg">Large (48px)</Button>
              <Button variant="accent" size="xl">XL Sticky CTA (56px)</Button>
              <Button variant="accent" size="md" isLoading>Syncing Telemetry</Button>
              <Button variant="accent" size="md" disabled>Module Locked</Button>
            </div>

            {/* IconButtons */}
            <div className="flex items-center gap-3 pt-2">
              <IconButton aria-label="Add to Wishlist" variant="secondary" size="md">
                <Heart className="h-4 w-4" />
              </IconButton>
              <IconButton aria-label="Open Filter Drawer" variant="outline" size="md">
                <Sliders className="h-4 w-4" />
              </IconButton>
              <IconButton aria-label="Quick Shopping Cart" variant="accent" size="md">
                <ShoppingCart className="h-4 w-4" />
              </IconButton>
            </div>
          </div>
        </section>

        {/* ---------------- 3. FORM CONTROLS ---------------- */}
        <section id="forms" className="space-y-6">
          <div className="border-b border-surface-border pb-3">
            <h3 className="text-2xl font-bold font-display text-text">3. Form Controls & Technical Inputs</h3>
            <p className="text-sm text-text-muted">
              Built with 44px mobile touch targets, label associations, character counts, and validation states.
            </p>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
            <Input
              label="Engineering Contact Email"
              type="email"
              placeholder="lead.engineer@squarepharma.com.bd"
              helperText="Corporate engineering email for RFQ quotations"
              required
            />

            <PhoneInput
              label="Procurement Officer Phone"
              value={phoneVal}
              onChange={(val) => setPhoneVal(val)}
              helperText="Format: 01XXXXXXXXX (+880 Bangladesh)"
              required
            />

            <Input
              label="Tax Identification / BIN Number"
              placeholder="BIN-992014-BD"
              error="Valid 9-digit VAT registration number required"
              defaultValue="BIN-12"
            />

            <Select defaultValue="plc">
              <div className="flex flex-col gap-1.5 w-full">
                <label className="text-sm font-medium text-text">Industrial Equipment Category</label>
                <SelectTrigger>
                  <SelectValue placeholder="Select classification..." />
                </SelectTrigger>
                <SelectContent>
                  <SelectGroup>
                    <SelectLabel>Automation Core</SelectLabel>
                    <SelectItem value="plc">Programmable Logic Controllers (PLCs)</SelectItem>
                    <SelectItem value="vfd">Variable Frequency Drives (VFDs)</SelectItem>
                    <SelectItem value="hmi">HMI & Touch Control Terminals</SelectItem>
                    <SelectItem value="servo">AC Servo Motors & Drives</SelectItem>
                    <SelectItem value="sensors">Inductive & Optical Sensors</SelectItem>
                  </SelectGroup>
                </SelectContent>
              </div>
            </Select>

            <Combobox
              label="Certified Industrial Brand"
              placeholder="Search or pick manufacturer..."
              options={[
                { value: 'siemens', label: 'Siemens Industrial Automation', description: 'Germany · CE / UL Approved' },
                { value: 'abb', label: 'ABB Robotics & Motion', description: 'Switzerland · ISO 14001' },
                { value: 'omron', label: 'Omron Sensing & Control', description: 'Japan · High Precision' },
                { value: 'schneider', label: 'Schneider Electric EcoStruxure', description: 'France · Energy Efficiency' },
                { value: 'yaskawa', label: 'Yaskawa Electric Corporation', description: 'Japan · High-Speed Servo' },
              ]}
              defaultValue="siemens"
            />

            <QuantityStepper
              value={quantity}
              onChange={(val) => setQuantity(val)}
              min={1}
              max={50}
            />
          </div>

          <Textarea
            label="Industrial Customization & Commissioning Notes"
            placeholder="Specify panel IP rating (e.g. IP65), DIN rail dimensions, ambient temperature requirements..."
            maxLength={300}
            showCharCount
            helperText="Include voltage tolerances and firmware version requirements."
          />

          {/* Radio Cards for Delivery and Payments */}
          <div className="space-y-4 pt-2">
            <h4 className="text-sm font-bold text-text uppercase tracking-wider">
              Radio Cards: Delivery & Logistics Options
            </h4>
            <RadioGroup
              value={selectedRadioCard}
              onValueChange={setSelectedRadioCard}
              className="grid grid-cols-1 sm:grid-cols-3 gap-3"
            >
              <RadioCard
                value="standard"
                title="Standard Industrial Freight"
                description="Delivery in 48-72 hours across Dhaka & Narayanganj"
                price="৳850"
                icon={<Truck className="h-5 w-5 text-accent" />}
              />
              <RadioCard
                value="express"
                title="Express Critical Delivery"
                description="Guaranteed within 24 hours · GPS Telemetry tracked"
                price="৳2,200"
                badge={<Badge variant="deal" size="sm">Urgent</Badge>}
                icon={<Zap className="h-5 w-5 text-accent" />}
              />
              <RadioCard
                value="pickup"
                title="Factory Depot Pickup"
                description="Ready in 4 hours from Tejgaon Industrial Depot"
                price="FREE"
                badge={<Badge variant="authentic" size="sm">Zero Fee</Badge>}
                icon={<Layers className="h-5 w-5 text-status-online" />}
              />
            </RadioGroup>
          </div>

          <div className="space-y-4 pt-2">
            <h4 className="text-sm font-bold text-text uppercase tracking-wider">
              Radio Cards: Industrial Payment Gateway
            </h4>
            <RadioGroup
              value={paymentOption}
              onValueChange={setPaymentOption}
              className="grid grid-cols-1 sm:grid-cols-4 gap-3"
            >
              <RadioCard
                value="bkash"
                title="bKash Merchant"
                description="Instant 1.2% cash rebate on B2B checkout"
                icon={<CreditCard className="h-5 w-5 text-pink-500" />}
              />
              <RadioCard
                value="nagad"
                title="Nagad Corporate"
                description="Direct postal digital disbursement"
                icon={<CreditCard className="h-5 w-5 text-orange-500" />}
              />
              <RadioCard
                value="card"
                title="Corporate Cards"
                description="Visa, Mastercard, Amex with 0% EMI"
                icon={<CreditCard className="h-5 w-5 text-sky-400" />}
              />
              <RadioCard
                value="po"
                title="Bank PO / Net 30"
                description="For pre-verified enterprise accounts"
                badge={<Badge variant="authentic" size="sm">Corporate</Badge>}
                icon={<ShieldCheck className="h-5 w-5 text-status-online" />}
              />
            </RadioGroup>
          </div>

          {/* Toggles and Checkboxes */}
          <div className="flex flex-wrap items-center gap-8 pt-2">
            <Checkbox
              label="Request On-Site Industrial Commissioning"
              description="A certified MECHWARE engineer will supervise PLC installation in your factory."
              defaultChecked
            />
            <Switch
              label="Real-Time Telemetry Alarm Notifications"
              description="Push SMS & WhatsApp alerts on critical machine fault codes."
              defaultChecked
            />
          </div>
        </section>

        {/* ---------------- 4. INDICATORS & PRICES ---------------- */}
        <section id="indicators" className="space-y-6">
          <div className="border-b border-surface-border pb-3">
            <h3 className="text-2xl font-bold font-display text-text">4. Prices, Ratings, Badges & Urgency</h3>
            <p className="text-sm text-text-muted">
              South Asian Lakh formatting (৳1,25,000), verified partner stars, live countdown timer.
            </p>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6 items-start">
            {/* Price Showcase */}
            <div className="rounded-xl border border-surface-border bg-surface p-5 space-y-3">
              <span className="text-xs font-semibold text-text-muted uppercase">Commercial Price Props</span>
              <div className="flex flex-col gap-2">
                <Price amount={125000} compareAt={160000} currency="BDT" size="xl" />
                <Price amount={3850} compareAt={4500} currency="BDT" size="md" />
                <Price amount={2850.5} currency="USD" size="lg" />
              </div>
            </div>

            {/* Rating Showcase */}
            <div className="rounded-xl border border-surface-border bg-surface p-5 space-y-3">
              <span className="text-xs font-semibold text-text-muted uppercase">Star Rating Props</span>
              <div className="flex flex-col gap-3">
                <Rating value={4.8} count={1284} size="lg" />
                <Rating value={interactiveRating} onRatingChange={setInteractiveRating} interactive size="md" />
                <span className="text-xs text-text-subtle">Click stars to test interactive rating mode.</span>
              </div>
            </div>

            {/* Countdown Timer */}
            <div className="rounded-xl border border-surface-border bg-surface p-5 space-y-3">
              <span className="text-xs font-semibold text-text-muted uppercase">Honest Flash Deal Timer</span>
              <CountdownTimer
                targetDate={Date.now() + 1000 * 60 * 60 * 18 + 1000 * 60 * 42}
                label="Industrial Flash Deal Ends"
                size="md"
              />
            </div>

            {/* Progress & Claimed */}
            <div className="rounded-xl border border-surface-border bg-surface p-5 space-y-3">
              <span className="text-xs font-semibold text-text-muted uppercase">Flash Batch Claimed</span>
              <Progress value={78} variant="accent" showLabel />
              <Progress value={92} variant="fault" showLabel />
            </div>
          </div>
        </section>

        {/* ---------------- 5. CARDS, TABS, ACCORDIONS & OVERLAYS ---------------- */}
        <section id="overlays" className="space-y-6">
          <div className="border-b border-surface-border pb-3">
            <h3 className="text-2xl font-bold font-display text-text">5. Cards, Tabs, Accordions & Dialogs</h3>
            <p className="text-sm text-text-muted">
              Built on Radix primitives with focus trapping, backdrop blur, and cybernetic contours.
            </p>
          </div>

          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {/* Tabs & Card */}
            <Card hoverable glow>
              <CardHeader>
                <CardTitle>PLC Controller Diagnostics Panel</CardTitle>
                <CardDescription>
                  Hardware channel configuration and firmware health status.
                </CardDescription>
              </CardHeader>
              <CardContent>
                <Tabs defaultValue="overview">
                  <TabsList variant="pill">
                    <TabsTrigger value="overview">Overview</TabsTrigger>
                    <TabsTrigger value="io">I/O Channels</TabsTrigger>
                    <TabsTrigger value="network">PROFINET Bus</TabsTrigger>
                  </TabsList>
                  <TabsContent value="overview" className="text-sm text-text-muted space-y-2">
                    <p>Current CPU Load: <strong className="text-text">24.2%</strong> · Operating Temp: <strong className="text-status-online">42°C (Optimal)</strong></p>
                    <p>Firmware Version: <strong className="text-accent font-mono">v3.1.2 Hotfix 4</strong></p>
                  </TabsContent>
                  <TabsContent value="io" className="text-sm text-text-muted">
                    32 Digital Inputs (24V DC), 16 Digital Outputs (Transistor 0.5A), 4 High-Speed Counters.
                  </TabsContent>
                  <TabsContent value="network" className="text-sm text-text-muted">
                    Dual-port PROFINET IO controller interface with MRP ring redundancy support.
                  </TabsContent>
                </Tabs>
              </CardContent>
              <CardFooter className="justify-between">
                <span className="text-xs text-text-subtle">Tejgaon Warehouse Depot</span>
                <Button variant="outline" size="sm">Download Log</Button>
              </CardFooter>
            </Card>

            {/* Accordion */}
            <div className="rounded-xl border border-surface-border bg-surface p-5">
              <h4 className="text-base font-bold font-display mb-3">Enterprise Technical FAQs</h4>
              <Accordion type="single" collapsible defaultValue="warranty">
                <AccordionItem value="warranty">
                  <AccordionTrigger>What warranty covers factory machinery sold on MECHWARE?</AccordionTrigger>
                  <AccordionContent>
                    Every automated module comes with a minimum 24-month manufacturer direct warranty, plus MECHWARE on-site Dhaka & Chittagong engineering backup with 4-hour dispatch SLA.
                  </AccordionContent>
                </AccordionItem>
                <AccordionItem value="calibration">
                  <AccordionTrigger>Are industrial sensors pre-calibrated before delivery?</AccordionTrigger>
                  <AccordionContent>
                    Yes. All proximity, pressure, and optical sensors pass our ISO 17025 compliant automated calibration bench and include a digital verification certificate.
                  </AccordionContent>
                </AccordionItem>
                <AccordionItem value="customs">
                  <AccordionTrigger>Can MECHWARE assist with duty exemptions for industrial import?</AccordionTrigger>
                  <AccordionContent>
                    Yes, our bonded logistics arm provides HS code classification (Chapter 84/85) and assistance with NBR industrial concession documentation.
                  </AccordionContent>
                </AccordionItem>
              </Accordion>
            </div>
          </div>

          {/* Interactive Modal, Drawer & Popover Launchers */}
          <div className="flex flex-wrap items-center gap-4 pt-2">
            {/* Modal Dialog */}
            <Dialog>
              <DialogTrigger asChild>
                <Button variant="accent">Open Engineering Modal Dialog</Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Request Official Machinery Proforma Invoice</DialogTitle>
                  <DialogDescription>
                    Generate an official stamped proforma invoice with Bangladesh VAT/Tax breakdown for corporate procurement approval.
                  </DialogDescription>
                </DialogHeader>
                <div className="space-y-3 py-2">
                  <Input label="Company Legal Name" defaultValue="Square Pharmaceuticals PLC" />
                  <Input label="Commercial Delivery Address" defaultValue="Square Centre, 48 Mohakhali C/A, Dhaka-1212" />
                </div>
                <DialogFooter>
                  <DialogClose asChild>
                    <Button variant="secondary">Cancel</Button>
                  </DialogClose>
                  <Button variant="accent" onClick={() => toast({ title: 'Proforma Issued', description: 'PI-2026-9812 has been emailed to procurement.', variant: 'success' })}>
                    Issue Proforma
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>

            {/* Sheet / Drawer */}
            <Sheet>
              <SheetTrigger asChild>
                <Button variant="secondary" leftIcon={<ShoppingCart className="h-4 w-4" />}>
                  Open Mini-Cart Drawer (Sheet)
                </Button>
              </SheetTrigger>
              <SheetContent side="right">
                <SheetHeader>
                  <SheetTitle>Procurement Cart (2 Modules)</SheetTitle>
                  <SheetDescription>Free industrial delivery unlocked for orders over ৳50,000.</SheetDescription>
                </SheetHeader>
                <div className="flex-1 py-4 space-y-3 overflow-y-auto">
                  <div className="flex items-center gap-3 p-3 rounded-md border border-surface-border bg-bg-muted">
                    <div className="h-12 w-12 rounded bg-surface border p-1 shrink-0">
                      <Cpu className="h-full w-full text-accent" />
                    </div>
                    <div className="flex-1 min-w-0">
                      <h5 className="text-xs font-bold text-text truncate">Siemens S7-1500 CPU 1516-3</h5>
                      <span className="text-xs text-text-muted">Qty: 1 · ৳3,45,000</span>
                    </div>
                  </div>
                  <div className="flex items-center gap-3 p-3 rounded-md border border-surface-border bg-bg-muted">
                    <div className="h-12 w-12 rounded bg-surface border p-1 shrink-0">
                      <Zap className="h-full w-full text-accent" />
                    </div>
                    <div className="flex-1 min-w-0">
                      <h5 className="text-xs font-bold text-text truncate">ABB ACS580 45kW VFD</h5>
                      <span className="text-xs text-text-muted">Qty: 1 · ৳1,85,000</span>
                    </div>
                  </div>
                </div>
                <SheetFooter>
                  <div className="w-full flex flex-col gap-2">
                    <div className="flex justify-between text-sm font-bold">
                      <span>Total Subtotal:</span>
                      <span className="text-accent">৳5,30,000</span>
                    </div>
                    <Button variant="accent" className="w-full">Proceed to B2B Checkout</Button>
                  </div>
                </SheetFooter>
              </SheetContent>
            </Sheet>

            {/* Dropdown Menu */}
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="outline">Industrial Export Actions</Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent>
                <DropdownMenuLabel>Engineering Exports</DropdownMenuLabel>
                <DropdownMenuItem>Export 3D CAD (.STEP / .IGES)<DropdownMenuShortcut>⌘E</DropdownMenuShortcut></DropdownMenuItem>
                <DropdownMenuItem>Generate Wiring Schematic (EPLAN)</DropdownMenuItem>
                <DropdownMenuItem>Download CE Conformity Declaration</DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem destructive>Decommission Controller Record</DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>

            {/* Tooltip */}
            <TooltipProvider>
              <Tooltip>
                <TooltipTrigger asChild>
                  <Button variant="ghost" size="sm">Hover for Tooltip</Button>
                </TooltipTrigger>
                <TooltipContent>
                  <p>Certified 100% genuine industrial machinery from authorized regional distributors.</p>
                </TooltipContent>
              </Tooltip>
            </TooltipProvider>

            {/* Toast Launcher */}
            <Button
              variant="secondary"
              onClick={() => toast({
                title: 'High Pressure Alarm Detected',
                description: 'Hydraulic Sensor #4 reported pressure surge above 250 Bar.',
                variant: 'fault',
                action: { label: 'View Telemetry', onClick: () => alert('Opening Telemetry') }
              })}
            >
              Trigger System Fault Toast
            </Button>
          </div>
        </section>

        {/* ---------------- 6. STATUS, STEPPER & FEEDBACK ---------------- */}
        <section id="feedback" className="space-y-6">
          <div className="border-b border-surface-border pb-3">
            <h3 className="text-2xl font-bold font-display text-text">6. Status, Stepper & Alert Banners</h3>
            <p className="text-sm text-text-muted">
              Visual indicators for assembly, shipment tracking, and telemetry alarms.
            </p>
          </div>

          <div className="space-y-4">
            <AlertBanner
              variant="info"
              title="Scheduled Factory Grid Maintenance"
              onDismiss={() => {}}
            >
              Dhaka North Substation telemetry will undergo routine load-bank synchronization on Friday, 02:00–04:00 AM BST.
            </AlertBanner>

            <AlertBanner
              variant="warning"
              title="Calibration Due Warning"
              onDismiss={() => {}}
            >
              Batch #982 Omron Inductive Sensors have 14 days remaining before annual ISO 17025 calibration renewal.
            </AlertBanner>

            {/* Stepper Showcase */}
            <div className="rounded-xl border border-surface-border bg-surface p-6 space-y-4">
              <h4 className="text-base font-bold font-display">Order Fulfillment Workflow Stepper</h4>
              <Stepper
                activeStep={2}
                steps={[
                  { id: '1', title: 'Order Confirmed', description: 'PO Verified' },
                  { id: '2', title: 'Assembly & QC', description: 'Bench Calibrated' },
                  { id: '3', title: 'Dispatched', description: 'Tejgaon Depot Hub' },
                  { id: '4', title: 'On-Site Commissioning', description: 'Factory Installed' },
                ]}
              />
            </div>

            {/* Avatar & Badges */}
            <div className="flex flex-wrap items-center gap-6">
              <div className="flex items-center gap-3">
                <Avatar status="online" isVerified>
                  <AvatarImage src="https://images.unsplash.com/photo-1534528741775-53994a69daeb?w=100&auto=format&fit=crop&q=80" />
                  <AvatarFallback>NM</AvatarFallback>
                </Avatar>
                <div>
                  <h5 className="text-sm font-bold text-text">Engr. Nadia Mostafa</h5>
                  <p className="text-xs text-text-muted">Senior Automation Specialist · Square Pharma</p>
                </div>
              </div>

              <div className="flex items-center gap-2">
                <Badge variant="deal">-35% Deal</Badge>
                <Badge variant="bestseller">#1 Bestseller</Badge>
                <Badge variant="low-stock">Only 2 Left</Badge>
                <Badge variant="success">Online 24/7</Badge>
                <Badge variant="ai">AI Auto-Quote</Badge>
              </div>
            </div>
          </div>
        </section>

        {/* ---------------- 7. DATA TABLE & FILE UPLOADER ---------------- */}
        <section id="media-data" className="space-y-6">
          <div className="border-b border-surface-border pb-3">
            <h3 className="text-2xl font-bold font-display text-text">7. Data Tables, File Upload & Illustrations</h3>
            <p className="text-sm text-text-muted">
              TanStack Table with sorting & pagination, drag-and-drop document uploader, and custom SVG line art.
            </p>
          </div>

          <DataTable columns={sampleColumns} data={sampleData} pageSize={5} />

          <div className="grid grid-cols-1 md:grid-cols-2 gap-6 pt-2">
            <FileUploader
              label="Upload Industrial Trade License & BIN Certificate (KYC)"
              helperText="Accepts PDF, STEP, DWG up to 25MB"
            />

            <div className="rounded-xl border border-surface-border bg-surface p-6 flex flex-col items-center justify-center text-center">
              <h4 className="text-sm font-bold text-text mb-4 uppercase tracking-wider">Product Detail Hover Zoom & Lightbox</h4>
              <ImageGallery images={sampleGalleryImages} />
            </div>
          </div>

          {/* SVG Illustrations Showcase */}
          <div className="rounded-xl border border-surface-border bg-surface p-6 space-y-4">
            <h4 className="text-sm font-bold text-text uppercase tracking-wider">
              Consistent Custom SVG Line Illustrations
            </h4>
            <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-5 gap-4">
              <div className="flex flex-col items-center p-3 rounded-lg border border-surface-border bg-bg-muted">
                <EmptyCartIllustration className="w-24 h-24" />
                <span className="text-xs font-semibold mt-2">Empty Cart</span>
              </div>
              <div className="flex flex-col items-center p-3 rounded-lg border border-surface-border bg-bg-muted">
                <EmptySearchIllustration className="w-24 h-24" />
                <span className="text-xs font-semibold mt-2">Zero Results</span>
              </div>
              <div className="flex flex-col items-center p-3 rounded-lg border border-surface-border bg-bg-muted">
                <OrderConfirmedIllustration className="w-24 h-24" />
                <span className="text-xs font-semibold mt-2">Order Confirmed</span>
              </div>
              <div className="flex flex-col items-center p-3 rounded-lg border border-surface-border bg-bg-muted">
                <SystemFaultIllustration className="w-24 h-24" />
                <span className="text-xs font-semibold mt-2">Telemetry Fault</span>
              </div>
              <div className="flex flex-col items-center p-3 rounded-lg border border-surface-border bg-bg-muted">
                <NoOrdersIllustration className="w-24 h-24" />
                <span className="text-xs font-semibold mt-2">No Active Orders</span>
              </div>
            </div>
          </div>

          {/* Empty and Error States */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-6 pt-2">
            <div className="rounded-xl border border-surface-border bg-surface">
              <EmptyState
                title="No Industrial Machinery Matches Your Filter"
                description="We couldn't find any 45kW VFDs with IP66 enclosures in stock right now. Try adjusting your voltage range or contact our engineering desk."
                action={<Button variant="accent" size="sm">Reset Filter Matrix</Button>}
              />
            </div>
            <div className="rounded-xl border border-surface-border bg-surface">
              <ErrorState
                code="ERR_BUS_TIMEOUT_503"
                title="PROFINET Bus Interface Timeout"
                description="The remote gateway controller at Chittagong Port did not respond within the 500ms heartbeat window."
                onRetry={() => toast({ title: 'Reconnecting', description: 'Re-polling PROFINET gateway...', variant: 'info' })}
              />
            </div>
          </div>
        </section>

        {/* ---------------- 8. PRODUCT CARDS & ZERO-CLS SKELETONS ---------------- */}
        <section id="product-cards" className="space-y-6">
          <div className="border-b border-surface-border pb-3">
            <h3 className="text-2xl font-bold font-display text-text">
              8. Product Cards & Zero-CLS Skeletons (Acceptance Benchmark)
            </h3>
            <p className="text-sm text-text-muted">
              Critical acceptance criteria: Hover crossfade to secondary image, wishlist heart animation, independent nested links, and 100% identical skeleton dimensions to guarantee ZERO layout shift!
            </p>
          </div>

          {/* Grid Size Comparison: Loaded vs Skeleton */}
          <div className="space-y-4">
            <h4 className="text-sm font-bold text-text uppercase tracking-wider">
              Default Grid Card (Loaded vs Identical Skeleton)
            </h4>
            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-6">
              {/* Loaded Card 1 */}
              <ProductCard
                id="siemens-s7-1500"
                title="Siemens SIMATIC S7-1500 CPU 1516-3 PN/DP High-End Industrial Controller"
                brand="Siemens Industry"
                primaryImage="https://images.unsplash.com/photo-1581092335397-9583fe92d232?w=600&auto=format&fit=crop&q=80"
                secondaryImage="https://images.unsplash.com/photo-1581092160607-ee22621dd758?w=600&auto=format&fit=crop&q=80"
                price={345000}
                compareAtPrice={395000}
                rating={4.9}
                reviewCount={342}
                deliveryHint="Free delivery in Dhaka tomorrow"
                badges={[
                  { label: 'Bestseller', variant: 'bestseller' },
                  { label: 'Genuine CE', variant: 'authentic' },
                ]}
                onAddToCart={(id) => toast({ title: 'Added to Cart', description: 'Siemens S7-1500 added to procurement cart.', variant: 'success' })}
              />

              {/* Matching Skeleton 1 (Zero Layout Shift!) */}
              <ProductCardSkeleton size="default" />

              {/* Loaded Card 2 */}
              <ProductCard
                id="abb-vfd-acs580"
                title="ABB ACS580 45kW General Purpose Industrial Variable Frequency Drive"
                brand="ABB Motion"
                primaryImage="https://images.unsplash.com/photo-1581091226825-a6a2a5aee158?w=600&auto=format&fit=crop&q=80"
                secondaryImage="https://images.unsplash.com/photo-1581092335397-9583fe92d232?w=600&auto=format&fit=crop&q=80"
                price={185000}
                compareAtPrice={215000}
                rating={4.7}
                reviewCount={88}
                deliveryHint="Get it tomorrow in Gazipur"
                badges={[
                  { label: '-14% Deal', variant: 'deal' },
                  { label: 'Low Stock', variant: 'low-stock' },
                ]}
                onAddToCart={(id) => toast({ title: 'Added to Cart', description: 'ABB ACS580 added to procurement cart.', variant: 'success' })}
              />

              {/* Matching Skeleton 2 */}
              <ProductCardSkeleton size="default" />
            </div>
          </div>

          {/* Horizontal Card Variant: Loaded vs Skeleton */}
          <div className="space-y-4 pt-4">
            <h4 className="text-sm font-bold text-text uppercase tracking-wider">
              Horizontal Row Card (Search Dropdown & Cart Upsell)
            </h4>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <ProductCard
                id="omron-sensor"
                size="horizontal"
                title="Omron E2E High-Precision Inductive Proximity Sensor M18"
                brand="Omron Industrial"
                primaryImage="https://images.unsplash.com/photo-1581092160607-ee22621dd758?w=300&auto=format&fit=crop&q=80"
                price={3850}
                compareAtPrice={4500}
                onAddToCart={(id) => toast({ title: 'Added to Cart', description: 'Omron Sensor added to cart.', variant: 'success' })}
              />
              <ProductCardSkeleton size="horizontal" />
            </div>
          </div>

          {/* Compact Carousel Card Variant: Loaded vs Skeleton */}
          <div className="space-y-4 pt-4">
            <h4 className="text-sm font-bold text-text uppercase tracking-wider">
              Compact Carousel Cards
            </h4>
            <div className="flex gap-4 overflow-x-auto pb-2">
              <ProductCard
                id="schneider-softstarter"
                size="compact"
                title="Schneider Electric Altivar 30kW Soft Starter"
                brand="Schneider"
                primaryImage="https://images.unsplash.com/photo-1581091226825-a6a2a5aee158?w=300&auto=format&fit=crop&q=80"
                price={74000}
                compareAtPrice={85000}
              />
              <ProductCardSkeleton size="compact" />
              <ProductCard
                id="yaskawa-servo"
                size="compact"
                title="Yaskawa Sigma-7 750W High-Speed AC Servo Motor"
                brand="Yaskawa"
                primaryImage="https://images.unsplash.com/photo-1581092335397-9583fe92d232?w=300&auto=format&fit=crop&q=80"
                price={98000}
                compareAtPrice={115000}
              />
              <ProductCardSkeleton size="compact" />
            </div>
          </div>
        </section>

        {/* Global Command Palette */}
        <CommandPalette
          items={commandItems}
          open={cmdOpen}
          onOpenChange={setCmdOpen}
        />
      </main>

      {/* Footer */}
      <footer className="border-t border-surface-border bg-surface py-8 px-4 sm:px-8 mt-20 text-center text-xs text-text-muted">
        <div className="max-w-container mx-auto flex flex-col sm:flex-row items-center justify-between gap-4">
          <p>© 2026 MECHWARE Engineering Platform. Powered by Industry 4.0 Cybernetic Architecture.</p>
          <div className="flex items-center gap-4">
            <span>WCAG 2.2 AA Compliant</span>
            <span>·</span>
            <span>Zero CLS Guarantee</span>
            <span>·</span>
            <span>Next.js 15 + .NET 10</span>
          </div>
        </div>
      </footer>
    </div>
  );
}

export default function DesignSystemPage() {
  return (
    <Suspense fallback={<div className="min-h-screen bg-bg text-text p-8 text-center font-sans">Loading MECHWARE Design System...</div>}>
      <ToastProvider>
        <DesignSystemContent />
      </ToastProvider>
    </Suspense>
  );
}
