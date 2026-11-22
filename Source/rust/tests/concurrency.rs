//! Several threads encoding and decoding different texts at once agree with the results of one thread.

mod common;

use common::{random_value, to_utf16, Rng};
use singulink_steno::{code_points, utf16};

#[test]
fn threads_agree_with_one_thread() {
    let mut rng = Rng::new(40);
    let values: Vec<Vec<u32>> = (0..2000).map(|_| random_value(&mut rng, 40, true)).collect();
    let expected: Vec<Vec<u8>> = values.iter().map(|v| code_points::encode(v).unwrap()).collect();

    std::thread::scope(|scope| {
        for t in 0..4 {
            let values = &values;
            let expected = &expected;

            scope.spawn(move || {
                // Each thread goes through the values in its own order, so the threads build and evict layouts at different times.
                for round in 0..3 {
                    for i in 0..values.len() {
                        let j = (i * (2 * t + 1) + round * 7) % values.len();
                        let bytes = utf16::encode(&to_utf16(&values[j]));
                        assert_eq!(bytes, expected[j]);
                        assert_eq!(code_points::decode(&bytes).unwrap(), values[j]);
                    }
                }
            });
        }
    });
}
