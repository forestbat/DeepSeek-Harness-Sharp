// cordis-shim.mjs — Cordis C# 宿主的 Node 伴生 shim。
// 主线程：stdio JSON-RPC 中继；worker 线程：运行插件代码，经 SharedArrayBuffer 同步 RPC 访问 C#。
// 句柄约定：负数 id 为 C# 侧对象，正数 id 为 JS 侧对象，0 为 null。
import { Worker, isMainThread, parentPort, workerData } from 'node:worker_threads'
import process from 'node:process'
import { pathToFileURL } from 'node:url'

const PAGE_SIZE = 256 * 1024
const CONTROL_BYTES = 64
const SYNC_TIMEOUT_MS = 60_000

const STATE_EMPTY = 0
const STATE_READY = 1
const STATE_CONSUMED = 2

if (isMainThread) {
  main()
} else {
  worker(workerData.sab)
}

function main() {
  const sab = new SharedArrayBuffer(CONTROL_BYTES + PAGE_SIZE)
  const control = new Int32Array(sab, 0, CONTROL_BYTES / 4)
  const data = new Uint8Array(sab, CONTROL_BYTES)
  const encoder = new TextEncoder()

  const worker = new Worker(new URL(import.meta.url), { workerData: { sab } })
  worker.unref()

  // 插件的 console 输出不能污染 stdio RPC 通道，转发为通知由 C# 决定去向
  worker.stdout.on('data', (chunk) => {
    send({ method: 'shim/stdout', params: { text: chunk.toString('utf8') } })
  })
  worker.stderr.on('data', (chunk) => {
    send({ method: 'shim/stderr', params: { text: chunk.toString('utf8') } })
  })

  let buffer = ''
  process.stdin.setEncoding('utf8')
  process.stdin.on('data', (chunk) => {
    buffer += chunk
    let index
    while ((index = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, index)
      buffer = buffer.slice(index + 1)
      if (line.trim().length === 0) continue
      let message
      try {
        message = JSON.parse(line)
      } catch {
        continue
      }
      onHostMessage(message)
    }
  })

  function send(message) {
    process.stdout.write(JSON.stringify(message) + '\n')
  }

  const pendingSync = new Set()

  function onHostMessage(message) {
    const isResponse = message.id !== undefined && message.method === undefined
    if (isResponse) {
      const seq = message.id
      if (pendingSync.delete(seq)) {
        writeSyncResponse(message)
      } else {
        worker.postMessage({ kind: 'response', seq, message })
      }
      return
    }
    worker.postMessage({ kind: 'request', message })
  }

  worker.on('message', (msg) => {
    if (msg.kind === 'request' && msg.sync) pendingSync.add(msg.message.id)
    send(msg.message)
  })

  worker.on('error', (error) => {
    send({ method: 'shim/error', params: { message: String(error?.stack || error) } })
  })

  async function writeSyncResponse(message) {
    const bytes = encoder.encode(JSON.stringify(message))
    let offset = 0
    Atomics.store(control, 1, bytes.length)
    while (true) {
      const chunk = Math.min(PAGE_SIZE, bytes.length - offset)
      data.set(bytes.subarray(offset, offset + chunk))
      Atomics.store(control, 2, chunk)
      Atomics.store(control, 0, STATE_READY)
      Atomics.notify(control, 0)
      offset += chunk
      if (offset >= bytes.length) return
      while (true) {
        const wait = Atomics.waitAsync(control, 0, STATE_READY)
        if (wait.async) await wait.value
        if (Atomics.load(control, 0) === STATE_CONSUMED) break
      }
      Atomics.store(control, 0, STATE_EMPTY)
    }
  }
}

function worker(sab) {
  const control = new Int32Array(sab, 0, CONTROL_BYTES / 4)
  const data = new Uint8Array(sab, CONTROL_BYTES)
  const decoder = new TextDecoder()

  const CTX_MARK = Symbol('cordis.ctxMark')
  const REMOTE_MARK = Symbol('cordis.remoteMark')

  let nextHandle = 1
  const handles = new Map()
  const handleIds = new WeakMap()

  function storeHandle(value) {
    let id = handleIds.get(value)
    if (id) return id
    id = nextHandle++
    handles.set(id, value)
    handleIds.set(value, id)
    return id
  }

  function marshal(value) {
    if (value === undefined) return { $u: 1 }
    if (value === null || typeof value === 'boolean' || typeof value === 'string') return value
    if (typeof value === 'number') return value
    if (typeof value === 'bigint') return { $bi: value.toString() }
    if (typeof value === 'object' || typeof value === 'function') {
      const ctxId = value[CTX_MARK]
      if (ctxId !== undefined) return { $h: ctxId }
      const remoteId = value[REMOTE_MARK]
      if (remoteId !== undefined) return { $h: remoteId }
    }
    if (typeof value === 'function') return { $h: storeHandle(value) }
    if (value instanceof Error) return { $e: value.message, stack: value.stack }
    if (value instanceof Promise) {
      const id = storeHandle(value)
      value.then(
        (result) => notify({ method: 'pres', params: { id, ok: true, result: marshal(result) } }),
        (error) => notify({ method: 'pres', params: { id, ok: false, result: marshal(error) } }),
      )
      return { $p: id }
    }
    if (Array.isArray(value)) return value.map(marshal)
    if (isPlainObject(value)) {
      const out = {}
      for (const key of Object.keys(value)) out[key] = marshal(value[key])
      return out
    }
    return { $h: storeHandle(value) }
  }

  function isPlainObject(value) {
    if (typeof value !== 'object' || value === null) return false
    const proto = Object.getPrototypeOf(value)
    return proto === Object.prototype || proto === null
  }

  function unmarshal(value) {
    if (value === null || typeof value !== 'object') return value
    if (Array.isArray(value)) return value.map(unmarshal)
    if (value.$u) return undefined
    if (value.$bi !== undefined) return BigInt(value.$bi)
    if (value.$e !== undefined) {
      const error = new Error(value.$e)
      if (value.stack) error.stack = value.stack
      return error
    }
    if (value.$p !== undefined) {
      const id = value.$p
      if (id > 0 && handles.get(id) instanceof Promise) return handles.get(id)
      return makeRemotePromise(id)
    }
    if (value.$h !== undefined) {
      const id = value.$h
      if (id > 0) return handles.get(id)
      return makeRemoteProxy(id)
    }
    const out = {}
    for (const key of Object.keys(value)) out[key] = unmarshal(value[key])
    return out
  }

  let nextSeq = 1
  const pending = new Map()
  const pendingPromises = new Map()

  function syncRequest(method, params) {
    const id = nextSeq++
    parentPort.postMessage({ kind: 'request', sync: true, message: { jsonrpc: '2.0', id, method, params } })
    const message = JSON.parse(readSyncResponse())
    if (message.error) {
      const error = new Error(message.error.message)
      error.stack = message.error.stack
      throw error
    }
    return message.result
  }

  function asyncRequest(method, params) {
    const id = nextSeq++
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject })
      parentPort.postMessage({ kind: 'request', sync: false, message: { jsonrpc: '2.0', id, method, params } })
    })
  }

  function notify(message) {
    parentPort.postMessage({ kind: 'notification', message })
  }

  function respond(message) {
    parentPort.postMessage({ kind: 'notification', message })
  }

  function readSyncResponse() {
    const deadline = Date.now() + SYNC_TIMEOUT_MS
    const chunks = []
    let total = -1
    let received = 0
    while (true) {
      while (Atomics.load(control, 0) !== STATE_READY) {
        const remaining = deadline - Date.now()
        if (remaining <= 0) throw new Error('cordis bridge: sync request timed out')
        Atomics.wait(control, 0, Atomics.load(control, 0), Math.min(remaining, 1000))
      }
      if (total < 0) total = Atomics.load(control, 1)
      const chunkLength = Atomics.load(control, 2)
      chunks.push(data.slice(0, chunkLength))
      received += chunkLength
      const done = received >= total
      Atomics.store(control, 0, done ? STATE_EMPTY : STATE_CONSUMED)
      Atomics.notify(control, 0)
      if (done) break
    }
    return decoder.decode(concat(chunks, received))
  }

  function concat(chunks, total) {
    const out = new Uint8Array(total)
    let offset = 0
    for (const chunk of chunks) {
      out.set(chunk, offset)
      offset += chunk.length
    }
    return out
  }

  const proxyCache = new Map()

  function makeRemoteProxy(id) {
    if (proxyCache.has(id)) return proxyCache.get(id)
    const target = function () {}
    target[REMOTE_MARK] = id
    const proxy = new Proxy(target, {
      get(_, prop) {
        if (typeof prop === 'symbol') {
          if (prop === Symbol.for('nodejs.util.inspect.custom')) return () => `Remote(${id})`
          return undefined
        }
        if (prop === 'then' || prop === 'constructor') return undefined
        return unmarshal(syncRequest('hget', { id, prop }))
      },
      set(_, prop, value) {
        syncRequest('hset', { id, prop, value: marshal(value) })
        return true
      },
      has(_, prop) {
        if (typeof prop === 'symbol') return false
        return syncRequest('hhas', { id, prop }) === true
      },
      apply(_, thisArg, args) {
        return unmarshal(syncRequest('hcall', { id, args: args.map(marshal) }))
      },
    })
    proxyCache.set(id, proxy)
    return proxy
  }

  function makeRemotePromise(id) {
    return new Promise((resolve, reject) => {
      pendingPromises.set(id, { resolve, reject })
    })
  }

  const CTX_METHODS = new Set([
    'on', 'once', 'emit', 'parallel', 'serial', 'bail', 'waterfall',
    'plugin', 'inject', 'effect', 'get', 'set', 'provide', 'accessor',
    'mixin', 'isolate', 'intercept', 'extend',
  ])

  const ctxCache = new Map()

  function makeCtx(id) {
    if (ctxCache.has(id)) return ctxCache.get(id)
    const target = { [CTX_MARK]: id }
    const proxy = new Proxy(target, {
      get(_, prop) {
        if (typeof prop === 'symbol') {
          if (prop === Symbol.for('nodejs.util.inspect.custom')) return () => `Context(${id})`
          return undefined
        }
        if (prop === 'then' || prop === 'constructor' || prop === 'prototype') return undefined
        if (CTX_METHODS.has(prop)) {
          return (...args) => {
            if (prop === 'plugin' && args.length > 0 && (typeof args[0] === 'function' || (args[0] && typeof args[0] === 'object' && !args[0][REMOTE_MARK] && !args[0][CTX_MARK]))) {
              const info = registerPlugin(args[0].default ?? args[0])
              args[0] = { $pk: info.key }
            }
            return unmarshal(syncRequest('ccall', { id, method: prop, args: args.map(marshal) }))
          }
        }
        return unmarshal(syncRequest('hget', { id, prop }))
      },
      set(_, prop, value) {
        syncRequest('hset', { id, prop, value: marshal(value) })
        return true
      },
      has(_, prop) {
        if (typeof prop === 'symbol') return false
        return syncRequest('hhas', { id, prop }) === true
      },
    })
    ctxCache.set(id, proxy)
    return proxy
  }

  const modules = new Map()
  const moduleKeys = new Map()

  const CORDIS_INIT = Symbol.for('cordis.init')
  const CORDIS_INIT_HOOKS = Symbol.for('cordis.initHooks')
  const ASYNC_GENERATOR_FUNCTION = (async function* () {}).constructor
  const GENERATOR_FUNCTION = (function* () {}).constructor

  function isConstructor(fn) {
    if (!fn.prototype) return false
    if (fn instanceof GENERATOR_FUNCTION) return false
    if (fn instanceof ASYNC_GENERATOR_FUNCTION) return false
    return true
  }

  async function importModule(specifier, baseUrl) {
    let url = specifier
    if (specifier.startsWith('.')) {
      url = new URL(specifier, baseUrl ?? pathToFileURL(process.cwd() + '/').href).href
    } else if (specifier.startsWith('/')) {
      url = pathToFileURL(specifier).href
    }
    if (moduleKeys.has(url)) return { ...modules.get(moduleKeys.get(url)).info, key: moduleKeys.get(url) }
    const ns = await import(url)
    let plugin = ns?.default ?? ns
    if (plugin && plugin.__esModule) plugin = plugin.default ?? plugin
    const registered = registerPlugin(plugin)
    moduleKeys.set(url, registered.key)
    return registered
  }

  function registerPlugin(plugin) {
    for (const [key, record] of modules) {
      if (record.plugin === plugin) return { ...record.info, key }
    }
    const callback = typeof plugin === 'function' ? plugin : plugin?.apply
    if (typeof callback !== 'function') throw new Error('invalid plugin')
    const key = `m${modules.size + 1}`
    const info = {
      name: plugin.name === 'apply' ? undefined : plugin.name,
      hasConfig: !!plugin.Config,
      inject: marshal(plugin.inject),
    }
    modules.set(key, { plugin, callback, info })
    return { ...info, key }
  }

  function resolvePlugin(id) {
    let plugin = handles.get(id)
    if (plugin && plugin.__esModule) plugin = plugin.default ?? plugin
    if (plugin && typeof plugin === 'object' && typeof plugin.apply !== 'function' && plugin.default) {
      plugin = plugin.default
    }
    return registerPlugin(plugin)
  }

  async function applyPlugin(key, ctxId, config) {
    const record = modules.get(key)
    if (!record) throw new Error(`unknown plugin ${key}`)
    const ctx = makeCtx(ctxId)
    const { plugin, callback } = record
    const disposes = []
    let result
    if (typeof plugin === 'function' && isConstructor(plugin)) {
      const instance = new plugin(ctx, config)
      for (const hook of instance?.[CORDIS_INIT_HOOKS] ?? []) hook()
      result = instance?.[CORDIS_INIT]?.()
    } else if (typeof plugin === 'function') {
      result = plugin(ctx, config)
    } else {
      result = callback.call(plugin, ctx, config)
    }
    await drainEffect(result, disposes)
    return { disposes }
  }

  async function drainEffect(result, disposes) {
    if (result == null) return
    if (typeof result === 'function') {
      disposes.push(storeHandle(result))
      return
    }
    if (typeof result.then === 'function') return drainEffect(await result, disposes)
    if (typeof result[Symbol.asyncIterator] === 'function') {
      for await (const item of result) {
        if (typeof item === 'function') disposes.push(storeHandle(item))
      }
      return
    }
    if (typeof result[Symbol.iterator] === 'function') {
      for (const item of result) {
        if (typeof item === 'function') disposes.push(storeHandle(item))
      }
    }
  }

  async function handleRequest(message) {
    const { id, method, params } = message
    try {
      const result = await dispatch(method, params ?? {})
      respond({ jsonrpc: '2.0', id, result: marshal(result) })
    } catch (error) {
      respond({
        jsonrpc: '2.0',
        id,
        error: { message: String(error?.message || error), stack: error?.stack },
      })
    }
  }

  function handleNotification(message) {
    const { method, params } = message
    if (method === 'pres') {
      const entry = pendingPromises.get(params.id)
      if (!entry) return
      pendingPromises.delete(params.id)
      if (params.ok) entry.resolve(unmarshal(params.result))
      else entry.reject(unmarshal(params.result))
    }
  }

  async function dispatch(method, params) {
    switch (method) {
      case 'import':
        return importModule(params.specifier, params.baseUrl)
      case 'resolvePlugin':
        return resolvePlugin(params.id)
      case 'runEffect': {
        const ctx = makeCtx(params.ctx)
        const fn = handles.get(params.cb)
        if (typeof fn !== 'function') throw new Error(`effect callback ${params.cb} not found`)
        const disposes = []
        await drainEffect(fn(ctx), disposes)
        return { disposes }
      }
      case 'apply':
        return applyPlugin(params.key, params.ctx, unmarshal(params.config))
      case 'validate':
        return validateConfig(params.key, unmarshal(params.config))
      case 'dispose': {
        let count = 0
        for (const handleId of params.handles ?? []) {
          const fn = handles.get(handleId)
          if (typeof fn === 'function') {
            count++
            await fn()
          }
        }
        return count
      }
      case 'cb': {
        const fn = handles.get(params.id)
        if (typeof fn !== 'function') throw new Error(`callback ${params.id} not found`)
        const args = (params.args ?? []).map(unmarshal)
        const thisArg = params.thisArg == null ? undefined : unmarshal(params.thisArg)
        return Reflect.apply(fn, thisArg, args)
      }
      case 'eval': {
        const ctx = makeCtx(params.ctx)
        const fn = new Function('ctx', 'expr', 'with (ctx) { return eval(expr) }')
        return fn(ctx, params.expr)
      }
      case 'hget': {
        return handles.get(params.id)?.[params.prop]
      }
      case 'hset': {
        handles.get(params.id)[params.prop] = unmarshal(params.value)
        return true
      }
      case 'hhas': {
        return params.prop in Object(handles.get(params.id))
      }
      case 'hcall': {
        const target = handles.get(params.id)
        const args = (params.args ?? []).map(unmarshal)
        return Reflect.apply(target, undefined, args)
      }
      default:
        throw new Error(`unknown method ${method}`)
    }
  }

  async function validateConfig(key, config) {
    const record = modules.get(key)
    const schema = record?.plugin?.Config
    if (!schema || !schema['~standard']) return { value: config }
    const result = schema['~standard'].validate(config)
    if (result instanceof Promise) throw new Error('Async config validation is not supported')
    if (result.issues) {
      return {
        issues: result.issues.map((issue) => ({
          message: issue.message,
          path: (issue.path ?? []).map((p) => (typeof p === 'object' && p !== null ? p.key : p)),
        })),
      }
    }
    return { value: result.value }
  }

  parentPort.on('message', (msg) => {
    if (msg.kind === 'request') {
      const message = msg.message
      if (message.id === undefined) {
        handleNotification(message)
      } else {
        handleRequest(message)
      }
    } else if (msg.kind === 'response') {
      const entry = pending.get(msg.seq)
      if (!entry) return
      pending.delete(msg.seq)
      if (msg.message.error) entry.reject(new Error(msg.message.error.message))
      else entry.resolve(msg.message.result)
    }
  })
}
