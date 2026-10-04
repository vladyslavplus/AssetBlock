export const doubled = (xs) => xs.map((x) => x * 2);
export class Box { #value; constructor(v) { this.#value = v; } get value() { return this.#value; } }
