// Reads text from an image with Apple's Vision framework (macOS 10.15+), run by osascript -l JavaScript.
// Input: the image bytes on standard input. Output: one line of JSON, {"Words":[...]} or {"Error":"..."}.
// Word boxes are normalized (0..1) with the origin at the bottom-left, as Vision reports them.
ObjC.import('Foundation');
ObjC.import('Vision');

function run() {
  try {
    const data = $.NSFileHandle.fileHandleWithStandardInput.readDataToEndOfFile;
    const handler = $.VNImageRequestHandler.alloc.initWithDataOptions(data, $({}));
    const request = $.VNRecognizeTextRequest.alloc.init;
    request.recognitionLevel = 0;            // accurate
    request.usesLanguageCorrection = false;  // values such as 99.1 or MD-4482 must not be "corrected"
    if (!handler.performRequestsError($([request]), null))
      return JSON.stringify({ Error: 'macOS could not recognize this image. Review the image crops.' });

    const words = [];
    const results = request.results;
    for (let line = 0; line < results.count; line++) {
      const candidates = results.objectAtIndex(line).topCandidates(1);
      if (candidates.count === 0)
        continue;
      const candidate = candidates.objectAtIndex(0);
      const text = candidate.string.js;
      const pattern = /\S+/g;
      let match;
      while ((match = pattern.exec(text)) !== null) {
        const observation = candidate.boundingBoxForRangeError($.NSMakeRange(match.index, match[0].length), null);
        if (!observation || observation.isNil())
          continue;
        const box = observation.boundingBox;
        words.push({ Text: match[0], X: box.origin.x, Y: box.origin.y, Width: box.size.width, Height: box.size.height });
      }
    }
    return JSON.stringify({ Words: words });
  } catch (error) {
    return JSON.stringify({ Error: 'macOS could not recognize this image. Review the image crops.' });
  }
}
