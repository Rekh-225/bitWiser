import { createReadStream, existsSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:http'
import { dirname, extname, join, normalize, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const port = Number(process.env.PORT ?? 4173)
const host = process.env.HOST ?? '0.0.0.0'
const here = dirname(fileURLToPath(import.meta.url))
const root = resolve(here, 'dist')
const commandPath = resolve(here, '..', '..', 'manual-commands.json')

const types = {
  '.css': 'text/css; charset=utf-8',
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
}

createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', `http://${request.headers.host}`)

  response.setHeader('Access-Control-Allow-Origin', '*')
  response.setHeader('Access-Control-Allow-Methods', 'GET,POST,OPTIONS')
  response.setHeader('Access-Control-Allow-Headers', 'Content-Type')

  if (request.method === 'OPTIONS') {
    response.statusCode = 204
    response.end()
    return
  }

  if (request.method === 'POST' && url.pathname === '/api/manual-command') {
    try {
      const body = await readBody(request)
      const command = JSON.parse(body)
      const saved = saveManualCommand(command)

      response.setHeader('Cache-Control', 'no-store')
      response.setHeader('Content-Type', 'application/json; charset=utf-8')
      response.end(JSON.stringify({ ok: true, command: saved }))
    } catch (error) {
      response.statusCode = 400
      response.setHeader('Content-Type', 'application/json; charset=utf-8')
      response.end(JSON.stringify({ ok: false, error: String(error?.message ?? error) }))
    }
    return
  }

  if (request.method === 'POST' && url.pathname === '/api/manual-mode') {
    try {
      const body = await readBody(request)
      const mode = JSON.parse(body)
      const saved = saveManualMode(mode)

      response.setHeader('Cache-Control', 'no-store')
      response.setHeader('Content-Type', 'application/json; charset=utf-8')
      response.end(JSON.stringify({ ok: true, manualMode: saved.manualMode }))
    } catch (error) {
      response.statusCode = 400
      response.setHeader('Content-Type', 'application/json; charset=utf-8')
      response.end(JSON.stringify({ ok: false, error: String(error?.message ?? error) }))
    }
    return
  }

  if (request.method === 'GET' && url.pathname === '/api/manual-commands') {
    response.setHeader('Cache-Control', 'no-store')
    response.setHeader('Content-Type', 'application/json; charset=utf-8')
    response.end(JSON.stringify(readCommandFile()))
    return
  }

  const requestedPath = normalize(decodeURIComponent(url.pathname)).replace(/^(\.\.[/\\])+/, '')
  let filePath = join(root, requestedPath)

  if (!existsSync(filePath) || statSync(filePath).isDirectory()) {
    filePath = join(root, 'index.html')
  }

  response.setHeader('Cache-Control', 'no-store')
  response.setHeader('Content-Type', types[extname(filePath)] ?? 'application/octet-stream')
  createReadStream(filePath).pipe(response)
}).listen(port, host, () => {
  console.log(`bitview listening at http://127.0.0.1:${port}`)
  if (host === '0.0.0.0') {
    console.log(`other devices can use http://<this-computer-ip>:${port}`)
  }
})

function readBody(request) {
  return new Promise((resolveBody, rejectBody) => {
    let body = ''

    request.on('data', (chunk) => {
      body += chunk
      if (body.length > 8192) {
        request.destroy()
        rejectBody(new Error('Request body too large'))
      }
    })

    request.on('end', () => resolveBody(body))
    request.on('error', rejectBody)
  })
}

function readCommandFile() {
  if (!existsSync(commandPath)) {
    return { manualMode: false, commands: [] }
  }

  try {
    const parsed = JSON.parse(readFileSync(commandPath, 'utf8'))
    return {
      manualMode: Boolean(parsed.manualMode),
      updatedBy: String(parsed.updatedBy ?? ''),
      updatedAt: String(parsed.updatedAt ?? ''),
      commands: Array.isArray(parsed.commands) ? parsed.commands : [],
    }
  } catch {
    return { manualMode: false, commands: [] }
  }
}

function saveManualMode(mode) {
  const current = readCommandFile()
  const saved = {
    ...current,
    manualMode: Boolean(mode.manualMode),
    updatedBy: String(mode.controller ?? 'manual'),
    updatedAt: new Date().toISOString(),
    commands: [],
  }

  writeFileSync(commandPath, JSON.stringify(saved, null, 2))
  return saved
}

function saveManualCommand(command) {
  const allowed = new Set(['Up', 'Down', 'Left', 'Right', 'ExtinguishFire', 'RefillWithWater', 'NOP'])
  const unitId = Number(command.unitId)
  const operation = String(command.operation ?? '')

  if (!Number.isInteger(unitId) || unitId <= 0) {
    throw new Error('Invalid unit id')
  }

  if (!allowed.has(operation)) {
    throw new Error('Invalid operation')
  }

  const current = readCommandFile()
  const nextSequence = Math.max(0, ...current.commands.map((item) => Number(item.sequence) || 0)) + 1
  const saved = {
    unitId,
    operation,
    sequence: nextSequence,
    controller: String(command.controller ?? 'manual'),
    issuedAt: new Date().toISOString(),
  }

  const commands = [
    saved,
    ...current.commands.filter((item) => Number(item.unitId) !== unitId),
  ].slice(0, 20)

  writeFileSync(commandPath, JSON.stringify({ ...current, commands }, null, 2))
  return saved
}
