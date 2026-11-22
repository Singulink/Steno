package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import org.junit.jupiter.api.Test;

/** Several threads encode and decode different text at once, sharing the layout cache, and agree with one thread. */
class ConcurrencyTest {
    @Test
    void threadsAgreeWithOneThread() throws Exception {
        RandomText random = new RandomText(40);
        int count = 2000;
        String[] texts = new String[count];
        byte[][] expected = new byte[count][];

        for (int i = 0; i < count; i++) {
            texts[i] = random.next(1 + random.random().nextInt(150));
            expected[i] = Steno.encode(texts[i]);
        }

        ExecutorService pool = Executors.newFixedThreadPool(4);

        try {
            List<Future<?>> futures = new ArrayList<>();

            for (int t = 0; t < 4; t++) {
                int start = t * 37;

                futures.add(pool.submit(() -> {
                    for (int round = 0; round < 5; round++) {
                        for (int k = 0; k < count; k++) {
                            int i = (start + k * 7 + round) % count;
                            assertArrayEquals(expected[i], Steno.encode(texts[i]));
                            assertEquals(texts[i], Steno.decode(expected[i]));
                        }
                    }

                    return null;
                }));
            }

            for (Future<?> future : futures) {
                future.get();
            }
        } finally {
            pool.shutdownNow();
        }
    }
}
