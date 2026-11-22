// Checks and normalises the arguments of the public functions, so that a wrong argument from untyped code throws instead of giving a silent
// wrong result, and so that the codec never calls code of the caller (an overridden method or getter of a subclass) while it works.
//
// This module is internal: the package's exports map does not expose it.

/**
 * The bytes that the decoding functions accept, as `TextDecoder` does: an `ArrayBuffer`, a `SharedArrayBuffer`, or any view of one (a typed
 * array of any kind or a `DataView`), read as the bytes of its range of the buffer.
 */
export type StenoBytes = ArrayBufferLike | ArrayBufferView;

// The intrinsic accessors, taken once, so that a subclass or an object that overrides them cannot run code here.
type Getter = (this: unknown) => unknown;

function getter(target: object, name: PropertyKey): Getter {
  return Object.getOwnPropertyDescriptor(target, name)!.get as Getter;
}

const TypedArrayPrototype = Object.getPrototypeOf(Uint8Array.prototype) as object;
const typedArrayTag = getter(TypedArrayPrototype, Symbol.toStringTag);
const typedArrayBuffer = getter(TypedArrayPrototype, 'buffer');
const typedArrayOffset = getter(TypedArrayPrototype, 'byteOffset');
const typedArrayLength = getter(TypedArrayPrototype, 'byteLength');
const dataViewBuffer = getter(DataView.prototype, 'buffer');
const dataViewOffset = getter(DataView.prototype, 'byteOffset');
const dataViewLength = getter(DataView.prototype, 'byteLength');
const arrayBufferLength = getter(ArrayBuffer.prototype, 'byteLength');
const sharedArrayBufferLength = typeof SharedArrayBuffer === 'function' ? getter(SharedArrayBuffer.prototype, 'byteLength') : null;
const PlainUint8Array = Uint8Array;
const plainPrototype = Uint8Array.prototype;

/**
 * Tells whether a value is a plain Uint8Array of this realm, cheaply: a real view whose constructor is Uint8Array. (Object.getPrototypeOf would
 * be exact but costs several times as much per call.) A subclass is read through a new plain view instead.
 */
function isPlainUint8Array(value: unknown): value is Uint8Array {
  return ArrayBuffer.isView(value) && (value as { constructor?: unknown }).constructor === PlainUint8Array;
}

/** Makes a plain Uint8Array over the bytes of a typed array, through the intrinsic accessors. */
function viewOfTypedArray(value: unknown): Uint8Array {
  const buffer = typedArrayBuffer.call(value) as ArrayBuffer;
  return new PlainUint8Array(buffer, typedArrayOffset.call(value) as number, typedArrayLength.call(value) as number);
}

/** Describes a value for an error message, without calling code of the value. */
function describe(value: unknown): string {
  if (value === null) return 'null';
  if (typeof value !== 'object' && typeof value !== 'function') return `a ${typeof value}`;
  const tag = typedArrayTag.call(value);
  return typeof tag === 'string' ? `an ${tag}` : `an object (${Object.prototype.toString.call(value)})`;
}

/** Tries an intrinsic accessor on a value; false if it throws because the value is not of that kind. */
function test(f: Getter, value: unknown): boolean {
  try {
    f.call(value);
    return true;
  } catch {
    return false;
  }
}

/**
 * Gets the bytes of an argument as a plain `Uint8Array` of this realm, over the same memory: the argument itself if it already is one, or a new
 * view of its range of the buffer.
 *
 * @throws TypeError if the argument is not an `ArrayBuffer`, a `SharedArrayBuffer`, a typed array or a `DataView`.
 */
export function toBytes(value: unknown, name: string): Uint8Array {
  // The common case first, cheaply: a plain Uint8Array of this realm. ArrayBuffer.isView is true only for a real typed array or DataView (not
  // for a proxy or a look-alike object).
  if (isPlainUint8Array(value)) return value;

  const tag = typedArrayTag.call(value);

  if (tag !== undefined) {
    // A typed array. The plain Uint8Array of this realm is used as it is; anything else (a subclass such as Buffer, another kind, another realm)
    // is read through a new plain view of its bytes.
    if (tag === 'Uint8Array' && Object.getPrototypeOf(value) === plainPrototype) return value as Uint8Array;
    return viewOfTypedArray(value);
  }

  if (test(dataViewBuffer, value)) {
    return new PlainUint8Array(dataViewBuffer.call(value) as ArrayBuffer, dataViewOffset.call(value) as number, dataViewLength.call(value) as number);
  }

  if (test(arrayBufferLength, value) || (sharedArrayBufferLength !== null && test(sharedArrayBufferLength, value))) {
    return new PlainUint8Array(value as ArrayBuffer);
  }

  throw new TypeError(`${name} must be an ArrayBuffer, a SharedArrayBuffer, a typed array or a DataView, not ${describe(value)}.`);
}

/**
 * Gets a destination for bytes as a plain `Uint8Array` of this realm over the same memory.
 *
 * @throws TypeError if the argument is not a `Uint8Array` (a subclass, such as Node's `Buffer`, is accepted).
 */
export function toDestination(value: unknown, name: string): Uint8Array {
  if (isPlainUint8Array(value)) return value;
  if (typedArrayTag.call(value) !== 'Uint8Array') throw new TypeError(`${name} must be a Uint8Array, not ${describe(value)}.`);
  if (Object.getPrototypeOf(value) === plainPrototype) return value as Uint8Array;
  return viewOfTypedArray(value);
}

/**
 * Checks that an argument is a string.
 *
 * @throws TypeError if it is not. Unlike `TextEncoder`, which converts any value with `String(value)`, the encoders take only strings: a number
 * or an object that reaches an encoder is far more often a bug than an intention.
 */
export function checkText(value: unknown, name: string): asserts value is string {
  if (typeof value !== 'string') throw new TypeError(`${name} must be a string, not ${describe(value)}.`);
}

/** Reads the `stream` option as `TextDecoder` does: any truthy value is true. */
export function streamOption(options: unknown): boolean {
  return options !== null && options !== undefined && Boolean((options as { stream?: unknown }).stream);
}
