import { JSDOM } from 'jsdom';

// Initialize full JSDOM environment
const dom = new JSDOM('<!doctype html><html><head></head><body><div id="root"></div></body></html>', {
  url: 'http://localhost:3000',
  pretendToBeVisual: true,
});

global.window = dom.window as any;
global.document = dom.window.document;
Object.defineProperty(globalThis, 'navigator', {
  value: dom.window.navigator,
  configurable: true,
  writable: true,
});
global.HTMLElement = dom.window.HTMLElement;
global.customElements = dom.window.customElements;
global.getComputedStyle = dom.window.getComputedStyle;
global.requestAnimationFrame = (cb) => setTimeout(cb, 0) as any;
global.cancelAnimationFrame = (id) => clearTimeout(id as any);

const domProps = [
  'HTMLFormElement',
  'HTMLInputElement',
  'HTMLButtonElement',
  'HTMLSelectElement',
  'HTMLTextAreaElement',
  'MutationObserver',
  'Node',
  'Element',
  'Event',
  'CustomEvent',
  'UIEvent',
  'MouseEvent',
  'KeyboardEvent',
  'FocusEvent',
  'PointerEvent',
];
for (const p of domProps) {
  if ((dom.window as any)[p]) {
    (global as any)[p] = (dom.window as any)[p];
  }
}
global.ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as any;
global.IntersectionObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as any;
(window.HTMLElement.prototype as any).scrollIntoView = () => {};
(window.HTMLElement.prototype as any).hasPointerCapture = () => false;
(window.HTMLElement.prototype as any).setPointerCapture = () => {};
(window.HTMLElement.prototype as any).releasePointerCapture = () => {};

(window as any).matchMedia =
  (window as any).matchMedia ||
  function (query: string) {
    return {
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    };
  };

import React from 'react';
import { render, fireEvent, cleanup } from '@testing-library/react';
import axe from 'axe-core';
import test, { afterEach } from 'node:test';
import assert from 'node:assert/strict';

afterEach(() => {
  cleanup();
});

import {
  Button,
  IconButton,
  Input,
  Textarea,
  Select,
  SelectTrigger,
  SelectValue,
  SelectContent,
  SelectItem,
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
  Dialog,
  DialogTrigger,
  DialogContent,
  DialogHeader,
  DialogTitle,
  Sheet,
  SheetTrigger,
  SheetContent,
  SheetHeader,
  SheetTitle,
  ToastProvider,
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
} from '../index';

async function auditAxe(container: HTMLElement, componentName: string) {
  const results = await axe.run(container, {
    rules: {
      // In JSDOM color contrast evaluation cannot calculate actual pixel rendering
      'color-contrast': { enabled: false },
    },
  });
  if (results.violations.length > 0) {
    console.error(`\n❌ Axe violations in [${componentName}]:`);
    for (const v of results.violations) {
      console.error(`  - [${v.id}] ${v.help}: ${v.nodes.map((n) => n.html).join(', ')}`);
    }
  }
  assert.equal(results.violations.length, 0, `${componentName} must have zero axe violations`);
}

test('Axe & Testing Library - Button & IconButton', async () => {
  let clicked = false;
  const { container } = render(
    <div>
      <Button variant="accent" size="md" onClick={() => (clicked = true)}>
        Request Quotation
      </Button>
      <Button variant="destructive" size="sm" disabled>
        Emergency Stop
      </Button>
      <IconButton aria-label="Settings configuration" variant="outline" size="md">
        <span>⚙</span>
      </IconButton>
    </div>
  );

  const btn = container.querySelector('button');
  assert.ok(btn);
  fireEvent.click(btn);
  assert.equal(clicked, true);

  await auditAxe(container, 'Button & IconButton');
});

test('Axe & Testing Library - Form Inputs (Input, Textarea, PhoneInput)', async () => {
  let phoneVal = '';
  const { container } = render(
    <div>
      <Input
        label="Commercial Tax ID / BIN"
        id="tax-id"
        placeholder="e.g. 001234567-0101"
        helperText="Required for corporate VAT exemption"
      />
      <Input
        label="Internal Reference Code"
        id="ref-code"
        error="Invalid code format. Expected MW-XXXX"
      />
      <Textarea
        label="Project Technical Scope"
        id="scope"
        maxLength={500}
        placeholder="Enter technical details..."
      />
      <PhoneInput
        label="Authorized Representative Phone"
        id="auth-phone"
        value={phoneVal}
        onChange={(v) => (phoneVal = v)}
      />
    </div>
  );

  await auditAxe(container, 'Input, Textarea, PhoneInput');
});

test('Axe & Testing Library - Checkbox, Radio, RadioCard, Switch', async () => {
  let switchChecked = false;
  let radioVal = 'standard';
  const { container } = render(
    <div>
      <Checkbox id="terms" label="I agree to Industrial Equipment Supply Terms" />
      <Switch
        id="auto-calibrate"
        label="Enable autonomous telemetry sync"
        checked={switchChecked}
        onCheckedChange={(c) => (switchChecked = c)}
      />
      <RadioGroup value={radioVal} onValueChange={(v) => (radioVal = v)} aria-label="Delivery Type">
        <Radio value="standard" label="Standard Freight (3-5 Days)" />
        <Radio value="express" label="Next-Day Dedicated Courier" />
      </RadioGroup>
      <RadioGroup defaultValue="bank-transfer" aria-label="Payment Method Options">
        <RadioCard
          value="bank-transfer"
          title="Industrial Wire Transfer (RTGS / BEFTN)"
          description="Zero surcharge for commercial accounts"
        />
        <RadioCard
          value="letter-of-credit"
          title="Commercial Letter of Credit (LC)"
          description="Available for industrial orders above 500,000 BDT"
        />
      </RadioGroup>
    </div>
  );

  await auditAxe(container, 'Checkbox, Radio, RadioCard, Switch');
});

test('Axe & Testing Library - QuantityStepper', async () => {
  let currentQty = 5;
  const { container } = render(
    <QuantityStepper
      value={currentQty}
      min={1}
      max={20}
      debounceMs={0}
      onChange={(v) => {
        currentQty = v;
      }}
      aria-label="Order Quantity"
    />
  );

  const plusBtn = container.querySelector('button[aria-label="Increase quantity"]');
  assert.ok(plusBtn);
  fireEvent.click(plusBtn);
  assert.equal(currentQty, 6);

  await auditAxe(container, 'QuantityStepper');
});

test('Axe & Testing Library - Price, Rating, Badge', async () => {
  const { container } = render(
    <div>
      <Price amount={125000} compareAt={160000} currency="BDT" size="lg" />
      <Rating value={4.8} count={1284} size="md" />
      <div className="flex gap-2">
        <Badge variant="deal">−23% DISCOUNT</Badge>
        <Badge variant="authentic">ISO 9001:2015</Badge>
        <Badge variant="low-stock">ONLY 2 LEFT</Badge>
        <Badge variant="ai">GEMINI ASSISTED</Badge>
      </div>
    </div>
  );

  await auditAxe(container, 'Price, Rating, Badge');
});

test('Axe & Testing Library - Card, Tabs, Accordion', async () => {
  const { container } = render(
    <div>
      <Card>
        <CardHeader>
          <CardTitle>Servo Controller Specs</CardTitle>
          <CardDescription>Industrial automation module specifications</CardDescription>
        </CardHeader>
        <CardContent>
          <p>Payload capacity: 250kg · Input: 400V 3-Phase</p>
        </CardContent>
        <CardFooter>
          <Button variant="outline" size="sm">
            Download CAD Drawing
          </Button>
        </CardFooter>
      </Card>

      <Tabs defaultValue="specs">
        <TabsList aria-label="Technical Details">
          <TabsTrigger value="specs">Specifications</TabsTrigger>
          <TabsTrigger value="compliance">Compliance</TabsTrigger>
        </TabsList>
        <TabsContent value="specs">400V 50Hz, IP67 enclosure rating</TabsContent>
        <TabsContent value="compliance">CE Certified, IEC 60204-1 compliant</TabsContent>
      </Tabs>

      <Accordion type="single" collapsible>
        <AccordionItem value="warranty">
          <AccordionTrigger>Industrial Warranty Coverage</AccordionTrigger>
          <AccordionContent>36 months comprehensive manufacturer warranty.</AccordionContent>
        </AccordionItem>
      </Accordion>
    </div>
  );

  await auditAxe(container, 'Card, Tabs, Accordion');
});

test('Axe & Testing Library - Tooltip, Popover, Dialog, Sheet', async () => {
  const { container } = render(
    <div>
      <TooltipProvider>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button variant="ghost">Hover for pinout info</Button>
          </TooltipTrigger>
          <TooltipContent>RS-485 Modbus RTU Pinout Diagram</TooltipContent>
        </Tooltip>
      </TooltipProvider>

      <Popover>
        <PopoverTrigger asChild>
          <Button variant="outline">Filter Dimensions</Button>
        </PopoverTrigger>
        <PopoverContent>
          <div>Filter options content</div>
        </PopoverContent>
      </Popover>

      <Dialog>
        <DialogTrigger asChild>
          <Button>Open RFQ Modal</Button>
        </DialogTrigger>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Request Formal Enterprise Quote</DialogTitle>
          </DialogHeader>
          <p>Specify quantity, delivery seaport or EPZ warehouse destination.</p>
        </DialogContent>
      </Dialog>
    </div>
  );

  await auditAxe(container, 'Tooltip, Popover, Dialog, Sheet');
});

test('Axe & Testing Library - AlertBanner, Breadcrumb, Pagination, Progress, Stepper', async () => {
  const { container } = render(
    <div>
      <AlertBanner variant="warning" title="Scheduled Maintenance">
        SCADA Cloud sync will be suspended on Sunday 02:00-04:00 AM UTC.
      </AlertBanner>

      <Breadcrumb>
        <BreadcrumbList>
          <BreadcrumbItem>
            <BreadcrumbLink href="/">Home</BreadcrumbLink>
          </BreadcrumbItem>
          <BreadcrumbSeparator />
          <BreadcrumbItem>
            <BreadcrumbLink href="/automation">Industrial Automation</BreadcrumbLink>
          </BreadcrumbItem>
          <BreadcrumbSeparator />
          <BreadcrumbItem>
            <BreadcrumbPage>Robotic Arms</BreadcrumbPage>
          </BreadcrumbItem>
        </BreadcrumbList>
      </Breadcrumb>

      <Pagination>
        <PaginationContent>
          <PaginationItem>
            <PaginationPrevious onClick={() => {}} />
          </PaginationItem>
          <PaginationItem>
            <PaginationLink onClick={() => {}} isActive>
              1
            </PaginationLink>
          </PaginationItem>
          <PaginationItem>
            <PaginationLink onClick={() => {}}>2</PaginationLink>
          </PaginationItem>
          <PaginationItem>
            <PaginationNext onClick={() => {}} />
          </PaginationItem>
        </PaginationContent>
      </Pagination>

      <Progress value={65} aria-label="Firmware Flashing Progress" />

      <Stepper
        steps={[
          { title: 'RFQ Submitted', description: 'Order specification logged' },
          { title: 'Engineering Review', description: 'CAD check completed' },
          { title: 'In Production', description: 'Fabrication in EPZ plant' },
          { title: 'Dispatched', description: 'Tracking active' },
        ]}
        currentStep={2}
      />
    </div>
  );

  await auditAxe(container, 'AlertBanner, Breadcrumb, Pagination, Progress, Stepper');
});

test('Axe & Testing Library - ProductCard (All 3 Sizes) & ProductCardSkeleton', async () => {
  const { container } = render(
    <div className="grid grid-cols-3 gap-4">
      <ProductCard
        id="mw-servo-200"
        title="Industrial AC Servo Motor 750W 3000RPM High-Precision"
        brand="MECHWARE DYNAMICS"
        primaryImage="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='100' height='100'%3E%3Crect width='100' height='100' fill='%23111'/%3E%3C/svg%3E"
        price={45000}
        compareAtPrice={52000}
        currency="BDT"
        size="default"
      />
      <ProductCard
        id="mw-plc-1200"
        title="Programmable Logic Controller 24VDC 14DI/10DO"
        brand="SIEMENS S7-1200"
        primaryImage="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='100' height='100'%3E%3Crect width='100' height='100' fill='%23111'/%3E%3C/svg%3E"
        price={68000}
        currency="BDT"
        size="compact"
      />
      <ProductCard
        id="mw-vfd-400"
        title="Variable Frequency Drive 5.5kW Inverter"
        brand="DANFOSS VLT"
        primaryImage="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='100' height='100'%3E%3Crect width='100' height='100' fill='%23111'/%3E%3C/svg%3E"
        price={38500}
        currency="BDT"
        size="horizontal"
      />
      <ProductCardSkeleton size="default" />
      <ProductCardSkeleton size="compact" />
      <ProductCardSkeleton size="horizontal" />
    </div>
  );

  await auditAxe(container, 'ProductCard & Skeleton');
});

test('Axe & Testing Library - EmptyState, ErrorState, Illustrations', async () => {
  const { container } = render(
    <div>
      <EmptyState
        title="No Commercial Orders Found"
        description="You have not placed any machinery procurement orders in this quarter."
        action={<Button variant="accent">Explore Machinery Catalog</Button>}
        illustration={<EmptyCartIllustration />}
      />
      <ErrorState
        title="Telemetry Server Unreachable"
        description="Failed to contact industrial gateway at 192.168.1.100. Connection timed out."
        onRetry={() => {}}
        illustration={<SystemFaultIllustration />}
      />
      <div className="flex gap-4">
        <EmptySearchIllustration aria-hidden="true" />
        <OrderConfirmedIllustration aria-hidden="true" />
        <NoOrdersIllustration aria-hidden="true" />
      </div>
    </div>
  );

  await auditAxe(container, 'EmptyState, ErrorState, Illustrations');
});

test('Axe & Testing Library - Select & Combobox', async () => {
  const { container } = render(
    <div>
      <Select defaultValue="dhaka">
        <SelectTrigger aria-label="EPZ Warehouse Location">
          <SelectValue placeholder="Select EPZ Warehouse" />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="dhaka">Dhaka EPZ (Savar Central Depot)</SelectItem>
          <SelectItem value="chittagong">Chittagong Port Logistics Hub</SelectItem>
          <SelectItem value="karnaphuli">Karnaphuli EPZ Custom Bonded</SelectItem>
        </SelectContent>
      </Select>

      <Combobox
        label="Industrial Protocol Interface"
        options={[
          { value: 'modbus', label: 'Modbus RTU / TCP' },
          { value: 'profinet', label: 'PROFINET Industrial Ethernet' },
          { value: 'ethercat', label: 'EtherCAT Realtime Bus' },
        ]}
        defaultValue="modbus"
      />
    </div>
  );

  await auditAxe(container, 'Select & Combobox');
});

test('Axe & Testing Library - DataTable & FileUploader', async () => {
  interface MachineSpec {
    id: string;
    model: string;
    power: string;
    rpm: number;
  }

  const columns: ColumnDef<MachineSpec>[] = [
    { accessorKey: 'id', header: 'Serial Number' },
    { accessorKey: 'model', header: 'Model Name' },
    { accessorKey: 'power', header: 'Rated Power' },
    { accessorKey: 'rpm', header: 'Max RPM' },
  ];

  const data: MachineSpec[] = [
    { id: 'MW-001', model: 'AC Servo 750W', power: '0.75 kW', rpm: 3000 },
    { id: 'MW-002', model: 'AC Servo 1500W', power: '1.50 kW', rpm: 3000 },
  ];

  const { container } = render(
    <div>
      <DataTable columns={columns} data={data} pageSize={5} />
      <FileUploader
        label="CAD STEP Drawing Upload"
        helperText="Max 25MB (.step, .dwg, .pdf)"
        accept=".step,.dwg,.pdf"
      />
    </div>
  );

  await auditAxe(container, 'DataTable & FileUploader');
});

test('Axe & Testing Library - CommandPalette, CountdownTimer, ImageGallery, Carousel', async () => {
  const futureDate = new Date(Date.now() + 1000 * 60 * 60 * 24 * 3); // 3 days in future

  const { container } = render(
    <div>
      <CountdownTimer targetDate={futureDate} label="Procurement tender closes in" />
      <ImageGallery
        images={[
          {
            src: 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" width="400" height="400"%3E%3Crect width="400" height="400" fill="%23222"/%3E%3C/svg%3E',
            alt: 'Servo Motor View 1',
          },
          {
            src: 'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" width="400" height="400"%3E%3Crect width="400" height="400" fill="%23333"/%3E%3C/svg%3E',
            alt: 'Servo Motor View 2',
          },
        ]}
      />
      <Carousel>
        <CarouselSlide>
          <div className="p-4 bg-surface border border-surface-border rounded-lg">Slide 1</div>
        </CarouselSlide>
        <CarouselSlide>
          <div className="p-4 bg-surface border border-surface-border rounded-lg">Slide 2</div>
        </CarouselSlide>
      </Carousel>
      <CommandPalette
        items={[
          { id: '1', title: 'Open Catalog', group: 'Navigation', onSelect: () => {} },
          { id: '2', title: 'Request RFQ', group: 'Actions', onSelect: () => {} },
        ]}
        open={false}
      />
    </div>
  );

  await auditAxe(container, 'CommandPalette, CountdownTimer, ImageGallery, Carousel');
});

