import { useEffect, useMemo, useRef, useState } from 'react'
import './App.css'

type Point = {
  x: number
  y: number
}

type Fire = Point & {
  hp: number
  age: number
  lastSeen: number
}

type Water = Point & {
  age: number
  lastSeen: number
}

type Unit = Point & {
  id: number
  type: 'Drone' | 'Truck' | 'Firefighter' | string
  water: number
  hp: number
  seenFires: number
  seenWaters: number
  state: string
  action: string
  thought: string
  stuck: number
  blocked: string[]
  target: (Point & { distance?: number; hp?: number }) | null
}

type Bounds = {
  minX: number
  maxX: number
  minY: number
  maxY: number
}

type HeatmapCell = Point & { age: number }

type FireState = {
  tick: number
  time: string
  units: Unit[]
  fires: Fire[]
  waters: Water[]
  heatmap?: HeatmapCell[]
  bounds: Bounds
}

type ManualOperation = 'Up' | 'Down' | 'Left' | 'Right' | 'ExtinguishFire' | 'RefillWithWater' | 'NOP'

const emptyState: FireState = {
  tick: 0,
  time: '-',
  units: [],
  fires: [],
  waters: [],
  bounds: { minX: 0, maxX: 120, minY: 0, maxY: 120 },
}

const unitLabel: Record<string, string> = {
  Drone: 'D',
  Truck: 'T',
  Firefighter: 'F',
}

const manualApiBase =
  window.location.port === '5173'
    ? `${window.location.protocol}//${window.location.hostname}:4173`
    : ''

function App() {
  const [state, setState] = useState<FireState>(emptyState)
  const [lastUpdate, setLastUpdate] = useState<Date | null>(null)
  const [zoom, setZoom] = useState(1)
  const [pan, setPan] = useState({ x: 0, y: 0 })
  const [selectedUnit, setSelectedUnit] = useState<number | null>(null)
  const [controlledUnit, setControlledUnit] = useState<number | null>(null)
  const [controllerName, setControllerName] = useState(() => window.localStorage.getItem('bitview-controller') ?? 'Player')
  const [manualMode, setManualMode] = useState(false)
  const [manualStatus, setManualStatus] = useState('Manual control ready')
  const [followUnit, setFollowUnit] = useState<number | null>(null)
  const [showWater, setShowWater] = useState(true)
  const [showTargets, setShowTargets] = useState(true)
  const [showLabels, setShowLabels] = useState(true)
  const [showStale, setShowStale] = useState(true)
  const [isDragging, setIsDragging] = useState(false)
  const dragStart = useRef({ x: 0, y: 0, panX: 0, panY: 0 })
  const mapRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    let alive = true

    async function loadState() {
      try {
        const response = await fetch(`/dashboard-state.json?t=${Date.now()}`, {
          cache: 'no-store',
        })

        if (!response.ok) return

        const nextState = (await response.json()) as FireState
        if (!alive) return

        setState(nextState)
        setLastUpdate(new Date())
      } catch {
        // The C# client may not have written state yet.
      }
    }

    loadState()
    const timer = window.setInterval(loadState, 400)

    return () => {
      alive = false
      window.clearInterval(timer)
    }
  }, [])

  const bounds = useMemo(() => paddedBounds(state.bounds), [state.bounds])
  const selected = state.units.find((unit) => unit.id === selectedUnit) ?? state.units[0]
  const controlled = state.units.find((unit) => unit.id === controlledUnit) ?? selected ?? null
  const topFires = showStale ? state.fires.slice(0, 14) : state.fires.filter((fire) => fire.age < 100).slice(0, 14)
  const recentWaters = state.waters.slice(0, 10)

  useEffect(() => {
    if (selectedUnit == null && state.units.length > 0) {
      setSelectedUnit(state.units[0].id)
    }

    if (controlledUnit == null && state.units.length > 0) {
      setControlledUnit(state.units[0].id)
    }
  }, [controlledUnit, selectedUnit, state.units])

  useEffect(() => {
    window.localStorage.setItem('bitview-controller', controllerName)
  }, [controllerName])

  useEffect(() => {
    let alive = true

    async function loadManualMode() {
      try {
        const response = await fetch(`${manualApiBase}/api/manual-commands?t=${Date.now()}`, {
          cache: 'no-store',
        })

        if (!response.ok) return

        const manual = await response.json()
        if (alive) setManualMode(Boolean(manual.manualMode))
      } catch {
        // Manual API is only available through serve-dist.mjs.
      }
    }

    loadManualMode()
    const timer = window.setInterval(loadManualMode, 1000)

    return () => {
      alive = false
      window.clearInterval(timer)
    }
  }, [])

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      const target = event.target as HTMLElement | null
      if (target && ['INPUT', 'SELECT', 'TEXTAREA'].includes(target.tagName)) return

      const operation = keyToOperation(event.key)
      if (!operation) return

      event.preventDefault()
      sendManualCommand(operation)
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [controlled?.id, controllerName, manualMode])

  useEffect(() => {
    if (followUnit == null || !mapRef.current) return

    const unit = state.units.find((item) => item.id === followUnit)
    if (!unit) return

    const map = mapRef.current.getBoundingClientRect()
    const px = toPct(unit.x, bounds.minX, bounds.maxX) / 100
    const py = toPct(unit.y, bounds.minY, bounds.maxY) / 100

    setPan({
      x: map.width / 2 - map.width * px * zoom,
      y: map.height / 2 - map.height * py * zoom,
    })
  }, [bounds, followUnit, state.units, zoom])

  function fitView() {
    setZoom(1)
    setPan({ x: 0, y: 0 })
    setFollowUnit(null)
  }

  function zoomBy(delta: number) {
    setZoom((value) => clamp(Number((value + delta).toFixed(2)), 0.45, 4))
  }

  function onPointerDown(event: React.PointerEvent<HTMLDivElement>) {
    setIsDragging(true)
    dragStart.current = {
      x: event.clientX,
      y: event.clientY,
      panX: pan.x,
      panY: pan.y,
    }
    event.currentTarget.setPointerCapture(event.pointerId)
  }

  function onPointerMove(event: React.PointerEvent<HTMLDivElement>) {
    if (!isDragging) return
    setFollowUnit(null)
    setPan({
      x: dragStart.current.panX + event.clientX - dragStart.current.x,
      y: dragStart.current.panY + event.clientY - dragStart.current.y,
    })
  }

  function onPointerUp(event: React.PointerEvent<HTMLDivElement>) {
    setIsDragging(false)
    event.currentTarget.releasePointerCapture(event.pointerId)
  }

  function onWheel(event: React.WheelEvent<HTMLDivElement>) {
    event.preventDefault()
    setFollowUnit(null)
    setZoom((value) => clamp(Number((value + (event.deltaY > 0 ? -0.08 : 0.08)).toFixed(2)), 0.45, 4))
  }

  async function sendManualCommand(operation: ManualOperation) {
    if (!manualMode) {
      setManualStatus('Turn manual mode ON first')
      return
    }

    if (!controlled) {
      setManualStatus('Pick a unit first')
      return
    }

    try {
      const response = await fetch(`${manualApiBase}/api/manual-command`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          unitId: controlled.id,
          operation,
          controller: controllerName.trim() || 'Player',
        }),
      })

      if (!response.ok) {
        setManualStatus(`Command failed: ${response.status}`)
        return
      }

      const label = operation === 'ExtinguishFire' ? 'Attack' : operation === 'RefillWithWater' ? 'Refill' : operation
      setManualStatus(`#${controlled.id} ${controlled.type}: ${label}`)
    } catch {
      setManualStatus('Open the viewer through node serve-dist.mjs for manual control')
    }
  }

  async function setManualOverride(enabled: boolean) {
    try {
      const response = await fetch(`${manualApiBase}/api/manual-mode`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          manualMode: enabled,
          controller: controllerName.trim() || 'Player',
        }),
      })

      if (!response.ok) {
        setManualStatus(`Manual mode failed: ${response.status}`)
        return
      }

      setManualMode(enabled)
      setManualStatus(enabled ? 'Manual mode ON: AI disabled' : 'Manual mode OFF: AI restored')
    } catch {
      setManualStatus('Open the viewer through node serve-dist.mjs for manual control')
    }
  }

  return (
    <main className="app-shell">
      <section className="tactical-board">
        <header className="topbar">
          <div>
            <p className="eyebrow">Bitwiser live operation</p>
            <h1>Fire response tactical viewer</h1>
          </div>
          <div className="status-strip">
            <Metric label="Tick" value={state.tick || '-'} />
            <Metric label="Server time" value={state.time || '-'} />
            <Metric label="Updated" value={lastUpdate ? lastUpdate.toLocaleTimeString() : 'waiting'} />
            <Metric label="Fires" value={state.fires.length} tone="fire" />
            <Metric label="Water" value={state.waters.length} tone="water" />
          </div>
        </header>

        <div className="toolbar" aria-label="Map controls">
          <button type="button" className="icon-button" onClick={() => zoomBy(-0.15)} title="Zoom out">
            -
          </button>
          <input
            aria-label="Zoom"
            className="zoom-slider"
            max="4"
            min="0.45"
            step="0.05"
            type="range"
            value={zoom}
            onChange={(event) => setZoom(Number(event.target.value))}
          />
          <button type="button" className="icon-button" onClick={() => zoomBy(0.15)} title="Zoom in">
            +
          </button>
          <button type="button" className="tool-button" onClick={fitView}>
            Fit
          </button>
          <label className="toggle">
            <input checked={showTargets} type="checkbox" onChange={(event) => setShowTargets(event.target.checked)} />
            Targets
          </label>
          <label className="toggle">
            <input checked={showWater} type="checkbox" onChange={(event) => setShowWater(event.target.checked)} />
            Water
          </label>
          <label className="toggle">
            <input checked={showLabels} type="checkbox" onChange={(event) => setShowLabels(event.target.checked)} />
            Labels
          </label>
          <label className="toggle">
            <input checked={showStale} type="checkbox" onChange={(event) => setShowStale(event.target.checked)} />
            Stale fire
          </label>
          <select
            aria-label="Follow unit"
            className="unit-select"
            value={followUnit ?? ''}
            onChange={(event) => setFollowUnit(event.target.value ? Number(event.target.value) : null)}
          >
            <option value="">Free pan</option>
            {state.units.map((unit) => (
              <option key={unit.id} value={unit.id}>
                Follow #{unit.id} {unit.type}
              </option>
            ))}
          </select>
        </div>

        <div
          ref={mapRef}
          className={`map-viewport ${isDragging ? 'dragging' : ''}`}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onWheel={onWheel}
        >
          <div
            className="map-world"
            style={{
              transform: `translate(${pan.x}px, ${pan.y}px) scale(${zoom})`,
            }}
          >
            <HeatmapCanvas heatmap={state.heatmap} bounds={bounds} zoom={zoom} />
            {showWater &&
              state.waters.map((water) => (
                <Marker
                  key={`w-${water.x}-${water.y}-${water.lastSeen}`}
                  bounds={bounds}
                  className="water-marker"
                  point={water}
                  title={`Water (${water.x},${water.y}) age ${water.age}`}
                />
              ))}
            {topFires.map((fire) => (
              <Marker
                key={`f-${fire.x}-${fire.y}-${fire.lastSeen}`}
                bounds={bounds}
                className={`fire-marker ${fire.hp < 250 ? 'critical' : ''} ${fire.age > 100 ? 'stale' : ''}`}
                point={fire}
                title={`Fire (${fire.x},${fire.y}) hp ${fire.hp} age ${fire.age}`}
              >
                {showLabels && <span>{fire.hp}</span>}
              </Marker>
            ))}
            {showTargets &&
              state.units.map((unit) =>
                unit.target ? (
                  <TargetLine key={`line-${unit.id}`} bounds={bounds} from={unit} to={unit.target} type={unit.type} />
                ) : null,
              )}
            {showTargets &&
              state.units.map((unit) =>
                unit.target ? (
                  <Marker key={`target-${unit.id}`} bounds={bounds} className="target-marker" point={unit.target} />
                ) : null,
              )}
            {state.units.map((unit) => (
              <Marker
                key={unit.id}
                bounds={bounds}
                className={`unit-marker ${unit.type} ${unit.stuck > 8 ? 'stuck' : ''} ${
                  selectedUnit === unit.id ? 'selected' : ''
                } ${controlledUnit === unit.id ? 'controlled' : ''}`}
                point={unit}
                title={`${unit.type} #${unit.id}: ${unit.action}`}
                onClick={(event) => {
                  event.stopPropagation()
                  setSelectedUnit(unit.id)
                  setControlledUnit((current) => current ?? unit.id)
                }}
              >
                <span>{unitLabel[unit.type] ?? 'U'}</span>
                {showLabels && <b>#{unit.id}</b>}
              </Marker>
            ))}
          </div>
        </div>
      </section>

      <aside className="control-room">
        <section className="panel manual-panel">
          <div className="section-heading">
            <div>
              <p className="eyebrow">Manual override</p>
              <h2>{controlled ? `Driving #${controlled.id} ${controlled.type}` : 'Pick a unit'}</h2>
            </div>
            <span className="manual-live">Live</span>
          </div>

          <button
            type="button"
            className={`manual-mode-button ${manualMode ? 'on' : ''}`}
            onClick={() => setManualOverride(!manualMode)}
          >
            {manualMode ? 'Manual mode ON - AI disabled' : 'Manual mode OFF - AI running'}
          </button>

          <div className="manual-form">
            <label>
              Controller
              <input
                value={controllerName}
                maxLength={18}
                onChange={(event) => setControllerName(event.target.value)}
              />
            </label>
            <label>
              Unit
              <select
                value={controlled?.id ?? ''}
                onChange={(event) => {
                  const id = Number(event.target.value)
                  setControlledUnit(id)
                  setSelectedUnit(id)
                }}
              >
                {state.units.map((unit) => (
                  <option key={unit.id} value={unit.id}>
                    #{unit.id} {unit.type}
                  </option>
                ))}
              </select>
            </label>
          </div>

          <div className="manual-pad" aria-label="Manual unit controls">
            <button disabled={!manualMode} type="button" className="pad-button up" onClick={() => sendManualCommand('Up')} title="Arrow up">
              Up
            </button>
            <button disabled={!manualMode} type="button" className="pad-button left" onClick={() => sendManualCommand('Left')} title="Arrow left">
              Left
            </button>
            <button disabled={!manualMode} type="button" className="pad-button stop" onClick={() => sendManualCommand('NOP')} title="Stop">
              Stop
            </button>
            <button disabled={!manualMode} type="button" className="pad-button right" onClick={() => sendManualCommand('Right')} title="Arrow right">
              Right
            </button>
            <button disabled={!manualMode} type="button" className="pad-button down" onClick={() => sendManualCommand('Down')} title="Arrow down">
              Down
            </button>
          </div>

          <div className="manual-actions">
            <button disabled={!manualMode} type="button" className="attack-button" onClick={() => sendManualCommand('ExtinguishFire')}>
              Attack fire
            </button>
            <button disabled={!manualMode} type="button" className="refill-button" onClick={() => sendManualCommand('RefillWithWater')}>
              Refill
            </button>
          </div>

          <p className="manual-hint">Turn manual mode ON to disable AI. Arrows move. Space attacks. R refills. N stops.</p>
          <p className="manual-status">{manualStatus}</p>
        </section>

        <section className="panel selected-panel">
          <div className="section-heading">
            <p className="eyebrow">Selected unit</p>
            <h2>{selected ? `#${selected.id} ${selected.type}` : 'No units yet'}</h2>
          </div>
          {selected ? (
            <UnitDetails unit={selected} onFollow={() => setFollowUnit(selected.id)} />
          ) : (
            <p className="empty-text">Waiting for the first unit packet.</p>
          )}
        </section>

        <section className="panel">
          <div className="section-heading compact">
            <h2>Units</h2>
            <span className="pill">{state.units.length}</span>
          </div>
          <div className="unit-list">
            {state.units.map((unit) => (
              <button
                key={unit.id}
                type="button"
                className={`unit-row ${unit.type} ${selectedUnit === unit.id ? 'active' : ''}`}
                onClick={() => {
                  setSelectedUnit(unit.id)
                  setControlledUnit(unit.id)
                }}
              >
                <span className="unit-dot">{unitLabel[unit.type] ?? 'U'}</span>
                <span>
                  <strong>#{unit.id} {unit.type}</strong>
                  <small>{unit.state} / {unit.action}</small>
                </span>
                <span className={unit.stuck > 8 ? 'warning' : ''}>{unit.stuck ? `stuck ${unit.stuck}` : 'moving'}</span>
              </button>
            ))}
          </div>
        </section>

        <section className="panel">
          <div className="section-heading compact">
            <h2>Fire priority</h2>
            <span className="pill fire-pill">{topFires.length}</span>
          </div>
          <div className="scroll-list">
            {topFires.length === 0 && <p className="empty-text">No known fires.</p>}
            {topFires.map((fire) => (
              <div className="intel-card fire-card" key={`${fire.x}-${fire.y}-${fire.lastSeen}`}>
                <strong>({fire.x}, {fire.y})</strong>
                <span>hp {fire.hp}</span>
                <span>age {fire.age}</span>
              </div>
            ))}
          </div>
        </section>

        <section className="panel">
          <div className="section-heading compact">
            <h2>Water memory</h2>
            <span className="pill water-pill">{state.waters.length}</span>
          </div>
          <div className="scroll-list">
            {recentWaters.length === 0 && <p className="empty-text">No known water.</p>}
            {recentWaters.map((water) => (
              <div className="intel-card water-card" key={`${water.x}-${water.y}-${water.lastSeen}`}>
                <strong>({water.x}, {water.y})</strong>
                <span>age {water.age}</span>
              </div>
            ))}
          </div>
        </section>
      </aside>
    </main>
  )
}

function Metric({ label, value, tone }: { label: string; value: string | number; tone?: 'fire' | 'water' }) {
  return (
    <div className={`metric ${tone ?? ''}`}>
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  )
}

function UnitDetails({ unit, onFollow }: { unit: Unit; onFollow: () => void }) {
  const target = unit.target ? `(${unit.target.x}, ${unit.target.y}) d=${unit.target.distance ?? '-'}` : '-'
  const blocked = unit.blocked.length ? unit.blocked.join(', ') : '-'

  return (
    <div className="details-grid">
      <Info label="Position" value={`(${unit.x}, ${unit.y})`} />
      <Info label="State" value={unit.state} />
      <Info label="Action" value={unit.action} strong />
      <Info label="Target" value={target} />
      <Info label="Water" value={unit.water} />
      <Info label="HP" value={unit.hp} />
      <Info label="Seen" value={`F ${unit.seenFires} / W ${unit.seenWaters}`} />
      <Info label="Blocked" value={blocked} />
      <Info label="Stuck" value={unit.stuck} strong={unit.stuck > 8} />
      <p className="thought-line">{unit.thought || 'No current decision text.'}</p>
      <button type="button" className="follow-button" onClick={onFollow}>
        Follow on map
      </button>
    </div>
  )
}

function Info({ label, value, strong }: { label: string; value: string | number; strong?: boolean }) {
  return (
    <div className="info-row">
      <span>{label}</span>
      <strong className={strong ? 'emphasis' : ''}>{value}</strong>
    </div>
  )
}

function HeatmapCanvas({ heatmap, bounds, zoom }: { heatmap?: HeatmapCell[]; bounds: Bounds; zoom: number }) {
  const canvasRef = useRef<HTMLCanvasElement>(null)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas || !heatmap) return
    const ctx = canvas.getContext('2d')
    if (!ctx) return

    const dpr = window.devicePixelRatio || 1
    const width = canvas.clientWidth
    const height = canvas.clientHeight

    canvas.width = width * dpr
    canvas.height = height * dpr
    ctx.scale(dpr, dpr)

    ctx.clearRect(0, 0, width, height)

    const spanX = Math.max(8, bounds.maxX - bounds.minX)
    const spanY = Math.max(8, bounds.maxY - bounds.minY)
    const cellW = width / spanX
    const cellH = height / spanY

    // Sort by age descending so newer (lower age) draws on top if overlapping
    const sorted = [...heatmap].sort((a, b) => b.age - a.age)

    for (const cell of sorted) {
      const px = ((cell.x - bounds.minX) / spanX) * width
      const py = ((cell.y - bounds.minY) / spanY) * height

      // Color logic: recent is bright green, older fades to dark blue/grey
      let color = ''
      if (cell.age < 50) color = '#10b981' // emerald
      else if (cell.age < 200) color = '#059669'
      else if (cell.age < 600) color = '#0f766e' // teal
      else if (cell.age < 1500) color = '#1e3a8a' // dark blue
      else color = '#172554'

      // Draw 3D block
      ctx.fillStyle = color
      ctx.fillRect(px, py, cellW - 1, cellH - 1)

      // Top highlight
      ctx.fillStyle = 'rgba(255,255,255,0.15)'
      ctx.fillRect(px, py, cellW - 1, Math.max(1, cellH * 0.1))

      // Number text if zoomed in enough
      if (zoom > 1.2 && cellW > 14) {
        ctx.fillStyle = 'rgba(255,255,255,0.7)'
        ctx.font = `600 ${Math.max(8, cellH * 0.4)}px Inter`
        ctx.textAlign = 'center'
        ctx.textBaseline = 'middle'
        ctx.fillText(cell.age.toString(), px + cellW / 2, py + cellH / 2)
      }
    }
  }, [heatmap, bounds, zoom])

  return (
    <canvas
      ref={canvasRef}
      style={{
        position: 'absolute',
        top: 0,
        left: 0,
        width: '100%',
        height: '100%',
        pointerEvents: 'none',
      }}
    />
  )
}

function Marker({
  bounds,
  children,
  className,
  point,
  title,
  onClick,
}: {
  bounds: Bounds
  children?: React.ReactNode
  className: string
  point: Point
  title?: string
  onClick?: React.MouseEventHandler<HTMLDivElement>
}) {
  return (
    <div
      className={`marker ${className}`}
      style={{
        left: `${toPct(point.x, bounds.minX, bounds.maxX)}%`,
        top: `${toPct(point.y, bounds.minY, bounds.maxY)}%`,
      }}
      title={title}
      onClick={onClick}
    >
      {children}
    </div>
  )
}

function TargetLine({ bounds, from, to, type }: { bounds: Bounds; from: Point; to: Point; type: string }) {
  const x1 = toPct(from.x, bounds.minX, bounds.maxX)
  const y1 = toPct(from.y, bounds.minY, bounds.maxY)
  const x2 = toPct(to.x, bounds.minX, bounds.maxX)
  const y2 = toPct(to.y, bounds.minY, bounds.maxY)
  const dx = x2 - x1
  const dy = y2 - y1
  const length = Math.sqrt(dx * dx + dy * dy)
  const angle = Math.atan2(dy, dx) * 180 / Math.PI

  return (
    <div
      className={`target-line ${type}`}
      style={{
        left: `${x1}%`,
        top: `${y1}%`,
        width: `${length}%`,
        transform: `rotate(${angle}deg)`,
      }}
    />
  )
}

function paddedBounds(bounds: Bounds): Bounds {
  const width = Math.max(20, bounds.maxX - bounds.minX)
  const height = Math.max(20, bounds.maxY - bounds.minY)
  const padX = Math.max(8, Math.round(width * 0.08))
  const padY = Math.max(8, Math.round(height * 0.08))

  return {
    minX: Math.max(0, bounds.minX - padX),
    maxX: bounds.maxX + padX,
    minY: Math.max(0, bounds.minY - padY),
    maxY: bounds.maxY + padY,
  }
}

function gridLines(min: number, max: number) {
  const span = Math.max(1, max - min)
  const step = span > 240 ? 40 : span > 120 ? 20 : 10
  const start = Math.ceil(min / step) * step
  const lines: number[] = []

  for (let value = start; value <= max; value += step) {
    lines.push(value)
  }

  return lines
}

function toPct(value: number, min: number, max: number) {
  if (max <= min) return 0
  return ((value - min) / (max - min)) * 100
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value))
}

function keyToOperation(key: string): ManualOperation | null {
  switch (key) {
    case 'ArrowUp':
    case 'w':
    case 'W':
      return 'Up'
    case 'ArrowDown':
    case 's':
    case 'S':
      return 'Down'
    case 'ArrowLeft':
    case 'a':
    case 'A':
      return 'Left'
    case 'ArrowRight':
    case 'd':
    case 'D':
      return 'Right'
    case ' ':
    case 'Enter':
      return 'ExtinguishFire'
    case 'r':
    case 'R':
      return 'RefillWithWater'
    case 'n':
    case 'N':
      return 'NOP'
    default:
      return null
  }
}

export default App
