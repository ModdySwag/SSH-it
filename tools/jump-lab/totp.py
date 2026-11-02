#!/usr/bin/env python3
"""RFC 6238 TOTP for the jump-lab secret. Prints the code, or waits for a fresh window first.

    python totp.py             # current code
    python totp.py --fresh     # wait until the step has at least 20 s left, then print
    python totp.py --wait      # print the code, then the seconds it stays valid
"""
import base64
import hashlib
import hmac
import os
import struct
import sys
import time

SECRET = os.environ.get("LAB_TOTP_SECRET", "J2CK5XZMQMCTO5BNRRVJV4EYXRY44IWK")


def code(secret=SECRET, at=None):
    counter = int((at if at is not None else time.time()) // 30)
    key = base64.b32decode(secret, casefold=True)
    digest = hmac.new(key, struct.pack(">Q", counter), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF
    return "%06d" % (value % 1_000_000)


def main():
    args = sys.argv[1:]
    if "--fresh" in args:
        # google-authenticator's default window accepts the previous/next step too, but a code with
        # 20 s left is enough for a human-paced typing test and keeps the timing honest.
        while True:
            remaining = 30 - (time.time() % 30)
            if remaining >= 20:
                break
            time.sleep(0.5)
    value = code()
    if "--wait" in args:
        print(value, "valid for %.0fs" % (30 - (time.time() % 30)))
    else:
        print(value)


if __name__ == "__main__":
    main()
