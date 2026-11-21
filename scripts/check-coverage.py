#!/usr/bin/env python3
"""Enforce the C# line coverage floor against one unfiltered coverlet report."""

import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path, help="directory containing one coverage.cobertura.xml")
    args = parser.parse_args()
    reports = list(args.results.rglob("coverage.cobertura.xml"))
    if len(reports) != 1:
        sys.exit(f"Expected exactly one coverage report in {args.results}, found {len(reports)}. Use a fresh results directory.")

    try:
        root = ET.parse(reports[0]).getroot()
        covered = int(root.attrib["lines-covered"])
        total = int(root.attrib["lines-valid"])
        branches = int(root.attrib["branches-covered"])
        total_branches = int(root.attrib["branches-valid"])
        if not (0 <= covered <= total and total > 0 and 0 <= branches <= total_branches and total_branches > 0):
            raise ValueError("invalid coverage counts")
    except (OSError, ET.ParseError, KeyError, ValueError) as error:
        sys.exit(f"Invalid coverage report: {error}")

    print(f"C# line coverage: {covered}/{total} ({100 * covered / total:.2f}%)")
    print(f"C# branch coverage: {branches}/{total_branches} ({100 * branches / total_branches:.2f}%)")
    # Compare integer counts, not coverlet's truncated line-rate or a rounded display value.
    if covered != total:
        sys.exit("C# line coverage is below the required 100%.")


if __name__ == "__main__":
    main()
