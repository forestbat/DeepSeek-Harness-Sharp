export const name = 'ping-plugin'

export function apply(ctx) {
  ctx.on('ping', (x) => x + 1)
  ctx.on('ping/async', async (x) => x * 2)
  ctx.on('ping/echo-args', (a, b) => ({ a, b }))
  ctx.logger.info('ping plugin applied')
  return () => {
    ctx.logger.info('ping plugin disposed')
  }
}
