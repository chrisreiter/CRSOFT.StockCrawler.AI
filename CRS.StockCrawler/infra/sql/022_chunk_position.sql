SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* Wo im Quelltext ein Abschnitt herkommt.

   Bisher hielt ein Abschnitt nur seine laufende Nummer und -- bei PDFs -- die
   Seite. Das reicht, um ihn wiederzufinden, aber nicht, um DORTHIN zu
   verweisen: Die Nummer sagt nichts über die Stelle im Text, und ohne Stelle
   lässt sich weder ein Zitat prüfen noch der Zusammenhang lesen.

   Ein Treffer ohne Fundstelle ist eine Behauptung. Wer ihn nachschlagen will,
   müsste das ganze Buch durchsehen -- und genau das soll die Suche ja
   ersparen. */

IF COL_LENGTH('dbo.knowledge_chunk', 'char_from') IS NULL
BEGIN
    ALTER TABLE dbo.knowledge_chunk ADD
        char_from INT NULL,
        char_to   INT NULL;
END;
GO

IF COL_LENGTH('dbo.knowledge_chunk', 'anchor') IS NULL
BEGIN
    /* Die ersten Worte des Abschnitts, für den Textanker im Browser.

       Chrome und Edge springen mit `#:~:text=…` an eine Textstelle. Das ist
       stabiler als eine Zeichenposition: Wird die Quelle nachträglich
       geändert, verschiebt sich jede Position, der Wortlaut aber meist nicht.
       Beides steht deshalb nebeneinander -- die Position für uns, der Anker
       für den Browser. */
    ALTER TABLE dbo.knowledge_chunk ADD anchor NVARCHAR(200) NULL;
END;
