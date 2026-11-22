/**
 * The Java reference implementation of the Steno Encoding, version 1.
 *
 * <p>{@link com.singulink.steno.Steno} encodes and decodes whole values and compares text in code point order.
 * {@link com.singulink.steno.StenoCharset} is the encoding as a {@link java.nio.charset.Charset}, for streams and every
 * API that takes a charset. {@link com.singulink.steno.StenoDecodeException} reports bytes that are not valid Steno
 * data.
 */
package com.singulink.steno;
