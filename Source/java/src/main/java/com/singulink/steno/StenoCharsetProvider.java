package com.singulink.steno;

import java.nio.charset.Charset;
import java.nio.charset.spi.CharsetProvider;
import java.util.Collections;
import java.util.Iterator;

/**
 * Provides {@link StenoCharset} to {@link Charset#forName(String)} under the name {@value Steno#CHARSET_NAME}.
 *
 * <p>The JDK finds it through the {@link java.util.ServiceLoader} mechanism: the module declares it with
 * {@code provides}, and the JAR lists it in {@code META-INF/services} for the class path. Applications do not use this
 * class directly.
 */
public final class StenoCharsetProvider extends CharsetProvider {
    /** Creates the provider. The {@link java.util.ServiceLoader} calls it. */
    public StenoCharsetProvider() {}

    /**
     * Returns an iterator over the one charset that this provider supports, {@link StenoCharset#INSTANCE}.
     *
     * @return the iterator
     */
    @Override
    public Iterator<Charset> charsets() {
        return Collections.<Charset>singletonList(StenoCharset.INSTANCE).iterator();
    }

    /**
     * Returns {@link StenoCharset#INSTANCE} for the name {@value Steno#CHARSET_NAME}, in any case, and {@code null} for
     * every other name.
     *
     * @param charsetName the name of the charset
     * @return the charset, or {@code null}
     */
    @Override
    public Charset charsetForName(String charsetName) {
        return Steno.CHARSET_NAME.equalsIgnoreCase(charsetName) ? StenoCharset.INSTANCE : null;
    }
}
