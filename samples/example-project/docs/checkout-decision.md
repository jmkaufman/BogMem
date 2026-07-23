# Checkout ownership

The checkout service owns order validation and payment orchestration. The
architecture keeps payment-provider details behind a gateway so callers depend
on the stable checkout contract.
