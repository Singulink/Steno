/**
 * The Java reference implementation of the Steno Encoding, version 1: a compact encoding of Unicode text whose bytes
 * sort like the text.
 *
 * <p>The module exports {@link com.singulink.steno} and provides the charset {@code x-steno} to
 * {@link java.nio.charset.Charset#forName} through {@link com.singulink.steno.StenoCharsetProvider}.
 */
module com.singulink.steno {
    exports com.singulink.steno;

    provides java.nio.charset.spi.CharsetProvider with
            com.singulink.steno.StenoCharsetProvider;
}
