import { Buffer } from 'node:buffer'
import fs from 'node:fs'
import protobuf from 'protobufjs'

(async () => {
  // const input_protobuf_encoded = 'CgMI2gUSEAoIc2NoaW5lc2UaAkhLIAEaAlAB'
  const input_protobuf_encoded = fs.readFileSync('electron/steam/test/protobuf/protobufTestResponseRaw', 'binary')

  const root = await protobuf.load([
    'electron/steam/test/protobuf/proto/service_storebrowse.proto',
    'electron/steam/test/protobuf/proto/common.proto',
    'electron/steam/test/protobuf/proto/common_base.proto',
  ])

  // const reqType = root.lookupType('CStoreBrowse_GetItems_Request')
  const reqType = root.lookupType('CStoreBrowse_GetItems_Response')

  // 1. Base64 → Uint8Array
  const raw = Buffer.from(input_protobuf_encoded, 'base64')

  // 2. decode
  const message = reqType.decode(raw)

  // 将 message 写入文件
  fs.writeFileSync('electron/steam/test/protobuf/protobufTestResponse', JSON.stringify(message.toJSON()))

  console.dir(message, { depth: null, maxArrayLength: null, colors: true })

  // 3. 转 JS 对象
  const object = reqType.toObject(message, {
    longs: Number,
    enums: Number,
    defaults: true,
  })

  // console.warn('Decoded request:', object)
})()
