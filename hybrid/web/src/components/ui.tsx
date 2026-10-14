import { useEffect, useRef, useState, type ReactNode } from "react";

// Claude のロゴに似せた放射状のマーク。応答中はゆっくり回る
export function Spark({ size = 32, spinning = false }: { size?: number; spinning?: boolean }) {
  const rays = [0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330];
  const lengths = [44, 36, 42, 34, 46, 38, 43, 35, 45, 37, 41, 39];
  return (
    <svg className={`spark${spinning ? " spinning" : ""}`} width={size} height={size} viewBox="0 0 100 100" aria-hidden>
      {rays.map((deg, i) => (
        <line
          key={deg}
          x1="50" y1="50" x2="50" y2={50 - lengths[i]}
          transform={`rotate(${deg + (i % 2) * 6} 50 50)`}
          stroke="currentColor" strokeWidth="9" strokeLinecap="round"
        />
      ))}
    </svg>
  );
}

export function useOutsideClick<T extends HTMLElement>(open: boolean, close: () => void) {
  const ref = useRef<T>(null);
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) close();
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && close();
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open, close]);
  return ref;
}

export function Dropdown({
  trigger,
  children,
  align = "left",
  direction = "down",
  className = "",
}: {
  trigger: (open: boolean, toggle: () => void) => ReactNode;
  children: (close: () => void) => ReactNode;
  align?: "left" | "right";
  direction?: "up" | "down";
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const close = () => setOpen(false);
  const ref = useOutsideClick<HTMLDivElement>(open, close);
  return (
    <div className="dropdown" ref={ref}>
      {trigger(open, () => setOpen((o) => !o))}
      {open && <div className={`menu menu-${align} menu-${direction} ${className}`}>{children(close)}</div>}
    </div>
  );
}

export function MenuItem({ icon, children, onClick, danger, checked, hint }: {
  icon?: ReactNode;
  children: ReactNode;
  onClick?: () => void;
  danger?: boolean;
  checked?: boolean;
  hint?: ReactNode;
}) {
  return (
    <button type="button" className={`menu-item${danger ? " danger" : ""}`} onClick={onClick}>
      {icon && <span className="menu-icon">{icon}</span>}
      <span className="menu-label">
        {children}
        {hint && <span className="menu-hint">{hint}</span>}
      </span>
      {checked !== undefined && <span className={`menu-check${checked ? " on" : ""}`}>✓</span>}
    </button>
  );
}

export function Toggle({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label?: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      className={`toggle${checked ? " on" : ""}`}
      onClick={(e) => {
        e.stopPropagation();
        onChange(!checked);
      }}
    >
      <span className="toggle-knob" />
    </button>
  );
}

export function Dialog({ title, children, onClose, footer, wide }: {
  title: string;
  children: ReactNode;
  onClose: () => void;
  footer?: ReactNode;
  wide?: boolean;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="dialog-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className={`dialog${wide ? " wide" : ""}`} role="dialog" aria-label={title}>
        <h2 className="dialog-title">{title}</h2>
        <div className="dialog-body">{children}</div>
        {footer && <div className="dialog-footer">{footer}</div>}
      </div>
    </div>
  );
}

export function relativeTime(ms: number) {
  const diff = (Date.now() - ms) / 1000;
  if (diff < 60) return "たった今";
  if (diff < 3600) return `${Math.floor(diff / 60)} 分前`;
  if (diff < 86400) return `${Math.floor(diff / 3600)} 時間前`;
  if (diff < 86400 * 30) return `${Math.floor(diff / 86400)} 日前`;
  return new Date(ms).toLocaleDateString("ja-JP");
}

export function usd(value: number, digits = 2) {
  if (value > 0 && value < 0.01) return "< $0.01";
  return `$${value.toLocaleString("en-US", { minimumFractionDigits: digits, maximumFractionDigits: digits })}`;
}

export function tokens(n: number) {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1000) return `${(n / 1000).toFixed(1)}k`;
  return String(n);
}
